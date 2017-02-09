﻿/*	
	Lorakon Sniff - Extract data from gamma spectrums and insert into database
    Copyright (C) 2017  Norwegian Radiation Protection Authority

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.
    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
// Authors: Dag Robole,

using System;
using System.IO;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Diagnostics;
using System.Xml.Serialization;
using System.Globalization;
using System.Data.SQLite;
using CTimer = System.Windows.Forms.Timer;

namespace LorakonSniff
{
    public partial class FormLorakonSniff : Form
    {
        private bool ApplicationInitalized = false;
        private ContextMenu trayMenu = null;
        private Log log = null;
        private Settings settings = null;
        private Monitor monitor = null;
        private ConcurrentQueue<FileEvent> events = null;
        private SQLiteConnection hashes = null;        
        private CTimer timer = null;
        private string ReportExecutable;
        private string ReportTemplate;
        private string ReportOutput;

        public FormLorakonSniff(NotifyIcon trayIcon)
        {
            InitializeComponent();            

            trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Avslutt", OnExit);
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Logg", OnLog);
            trayMenu.MenuItems.Add("Innstillinger", OnSettings);
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Informasjon", OnAbout);

            trayIcon.Text = "Lorakon Sniff";
            trayIcon.Icon = Properties.Resources.LorakonIcon;

            trayIcon.ContextMenu = trayMenu;
            trayIcon.Visible = true;
        }        

        private void FormLorakonSniff_Load(object sender, EventArgs e)
        {
            log = new Log();
            if (!log.Create())
            {
                MessageBox.Show("Kan ikke opprette logg database");
                Application.Exit();
            }
            log.AddMessage("Starting log service");

            string InstallationDirectory = Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]) + Path.DirectorySeparatorChar;
            ReportExecutable = InstallationDirectory + "report.exe";
            if (!File.Exists(ReportExecutable))
            {
                log.AddMessage("Finner ikke filen: " + ReportExecutable);
                Application.Exit();
            }

            ReportTemplate = InstallationDirectory + "report_template.tpl";
            if (!File.Exists(ReportTemplate))
            {
                log.AddMessage("Finner ikke filen: " + ReportTemplate);                
                Application.Exit();
            }            

            Visible = false;
            ShowInTaskbar = false;

            // Set default window layout
            Rectangle rect = Screen.FromControl(this).Bounds;
            Width = (rect.Right - rect.Left) / 2;
            Height = (rect.Bottom - rect.Top) / 2;
            Left = rect.Left + Width / 2;
            Top = rect.Top + Height / 2;

            TimeSpan OneDay = new TimeSpan(1, 0, 0, 0);
            dtLogFrom.Value = DateTime.Now - OneDay;
            dtLogTo.Value = DateTime.Now + OneDay;

            // Create environment and load settings
            if (!Directory.Exists(LorakonEnvironment.SettingsPath))
                Directory.CreateDirectory(LorakonEnvironment.SettingsPath);
            settings = new Settings();
            LoadSettings();

            ReportOutput = LorakonEnvironment.SettingsPath + Path.DirectorySeparatorChar + "last_report.rpt";

            tbSettingsWatchDirectory.Text = settings.WatchDirectory;
            tbSettingsConnectionString.Text = settings.ConnectionString;
            tbSettingsSpectrumFilter.Text = settings.FileFilter;

            if (!Directory.Exists(settings.WatchDirectory))
                Directory.CreateDirectory(settings.WatchDirectory);

            events = new ConcurrentQueue<FileEvent>();
            hashes = Hashes.Create();

            // Handle files that has been created after last shutdown and has not been handled before
            Hashes.Open(hashes);
            foreach (string fname in Directory.EnumerateFiles(settings.WatchDirectory, settings.FileFilter, SearchOption.AllDirectories))
            {
                string sum = FileOps.GetChecksum(fname);
                if (!Hashes.HasChecksum(hashes, sum))
                {
                    log.AddMessage("Importing " + fname + " [" + sum + "]");

                    string repfile = GenerateReport(fname);
                    SpectrumReport report = ParseReport(repfile);
                    StoreReport(report);

                    Hashes.InsertChecksum(hashes, sum);
                }                
            }
            Hashes.Close(ref hashes);

            // Start timer for processing file events
            timer = new CTimer();
            timer.Interval = 500;
            timer.Tick += timer_Tick;
            timer.Start();

            // Start monitoring file events
            monitor = new Monitor(settings, events);
            monitor.Start();
        }

        void timer_Tick(object sender, EventArgs e)
        {            
            while (!events.IsEmpty)
            {
                FileEvent evt;
                if (events.TryDequeue(out evt))
                {
                    if (!File.Exists(evt.FullPath)) // This happens when the same event are reported more than once
                        continue;
                    
                    string sum = FileOps.GetChecksum(evt.FullPath);

                    Hashes.Open(hashes);
                    if (!Hashes.HasChecksum(hashes, sum))                    
                    {
                        log.AddMessage("Importing " + evt.FullPath + " [" + sum + "]");

                        string repfile = GenerateReport(evt.FullPath);
                        SpectrumReport report = ParseReport(repfile);
                        StoreReport(report);

                        Hashes.InsertChecksum(hashes, sum);
                    }                    
                    Hashes.Close(ref hashes);
                }
            }
        } 
       
        private string GenerateReport(string specfile)
        {   
            // Generate report
                     
            string args = specfile + " /TEMPLATE=" + ReportTemplate + " /SECTION=\"\" /NEWFILE /OUTFILE=" + ReportOutput;
            Process p = new Process();
            p.StartInfo.FileName = ReportExecutable;
            p.StartInfo.Arguments = args;            
            p.StartInfo.WorkingDirectory = Path.GetDirectoryName(ReportOutput);
            p.StartInfo.UseShellExecute = true;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p.Start();
            p.WaitForExit();

            return ReportOutput;            
        }

        private SpectrumReport ParseReport(string repfile)
        {
            SpectrumReport report = new SpectrumReport();            

            TextReader reader = File.OpenText(repfile);
            string line, param;
            while((line = reader.ReadLine()) != null)
            {
                line = line.Trim();                

                if((param = ParseReport_ExtractParameter("Laboratory", line)) != String.Empty)                
                    report.Laboratory = param;

                else if ((param = ParseReport_ExtractParameter("Operator", line)) != String.Empty)
                    report.Operator = param;

                else if ((param = ParseReport_ExtractParameter("Sample Title", line)) != String.Empty)
                    report.SampleTitle = param;

                else if ((param = ParseReport_ExtractParameter("Sample Identification", line)) != String.Empty)
                    report.SampleIdentification = param;

                else if ((param = ParseReport_ExtractParameter("Sample Type", line)) != String.Empty)
                    report.SampleType = param;

                else if ((param = ParseReport_ExtractParameter("Sample Component", line)) != String.Empty)
                    report.SampleComponent = param;

                else if ((param = ParseReport_ExtractParameter("Sample Geometry", line)) != String.Empty)
                    report.SampleGeometry = param;

                else if ((param = ParseReport_ExtractParameter("Sample Location Type", line)) != String.Empty)
                    report.SampleLocationType = param;

                else if ((param = ParseReport_ExtractParameter("Sample Location", line)) != String.Empty)
                    report.SampleLocation = param;

                else if ((param = ParseReport_ExtractParameter("Sample Community/County", line)) != String.Empty)
                    report.SampleCommunityCounty = param;

                else if ((param = ParseReport_ExtractParameter("Sample Coordinates", line)) != String.Empty)
                {
                    char[] wspace = new char[] { ' ', '\t' };
                    string[] coords = param.Split(wspace, StringSplitOptions.RemoveEmptyEntries);
                    if (coords.Length > 0)
                        report.SampleLatitude = Convert.ToDouble(coords[0], CultureInfo.InvariantCulture);
                    if (coords.Length > 1)
                        report.SampleLongitude = Convert.ToDouble(coords[1], CultureInfo.InvariantCulture);
                    if (coords.Length > 2)
                        report.SampleAltitude = Convert.ToDouble(coords[2], CultureInfo.InvariantCulture);
                }

                else if ((param = ParseReport_ExtractParameter("Sample Comment", line)) != String.Empty)
                    report.Comment = param;

                else if ((param = ParseReport_ExtractParameter("Sample Size/Error", line)) != String.Empty)
                {
                    char[] wspace = new char[] { ' ', '\t' };
                    string[] items = param.Split(wspace, StringSplitOptions.RemoveEmptyEntries);
                    if (items.Length > 0)
                        report.SampleSize = Convert.ToDouble(items[0], CultureInfo.InvariantCulture);
                    if (items.Length > 1)
                        report.SampleError = Convert.ToDouble(items[1], CultureInfo.InvariantCulture);
                    if (items.Length > 2)
                        report.SampleUnit = items[2]; // FIXME vv/tv
                }

                else if ((param = ParseReport_ExtractParameter("Sample Taken On", line)) != String.Empty)
                    report.SampleTime = Convert.ToDateTime(param);

                else if ((param = ParseReport_ExtractParameter("Acquisition Started", line)) != String.Empty)
                    report.AcquisitionTime = Convert.ToDateTime(param);

                else if ((param = ParseReport_ExtractParameter("Live Time", line)) != String.Empty)
                    report.Livetime = Convert.ToDouble(param, CultureInfo.InvariantCulture);

                else if ((param = ParseReport_ExtractParameter("Real Time", line)) != String.Empty)
                    report.Realtime = Convert.ToDouble(param, CultureInfo.InvariantCulture);

                else if ((param = ParseReport_ExtractParameter("Dead Time", line)) != String.Empty)
                    report.Deadtime = Convert.ToDouble(param, CultureInfo.InvariantCulture);

                else if ((param = ParseReport_ExtractParameter("Nuclide Library Used", line)) != String.Empty)
                    report.NuclideLibrary = param;

                else if (line.StartsWith("+++INTR+++"))                
                    ParseReport_INTR(reader, report);                

                else if (line.StartsWith("+++MDA+++"))                
                    ParseReport_MDA(reader, report);                
            }            

            return report;
        }

        private void ParseReport_INTR(TextReader reader, SpectrumReport report)
        {
            report.Results.Clear();
            string line;
            char[] wspace = new char[] { ' ', '\t' };
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("---INTR---"))
                    return;
                
                string[] items = line.Split(wspace, StringSplitOptions.RemoveEmptyEntries);
                if(items.Length == 6)
                {
                    SpectrumResult result = new SpectrumResult();
                    result.NuclideName = items[0].Trim();
                    result.Activity = Convert.ToDouble(items[4], CultureInfo.InvariantCulture);
                    result.ActivityUncertainty = Convert.ToDouble(items[5], CultureInfo.InvariantCulture);
                    report.Results.Add(result);
                }
            }
        }

        private void ParseReport_MDA(TextReader reader, SpectrumReport report)
        {
            string line;
            char[] wspace = new char[] { ' ', '\t' };
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("---MDA---"))
                    return;

                string[] items = line.Split(wspace, StringSplitOptions.RemoveEmptyEntries);
                if (items.Length == 7)
                {
                    string nuclname = items[0].Trim();
                    SpectrumResult r = report.Results.Find(x => x.NuclideName == nuclname);
                    if (r != null)
                        r.MDA = Convert.ToDouble(items[4].Trim(), CultureInfo.InvariantCulture);
                }
            }
        }

        private string ParseReport_ExtractParameter(string tag, string line)
        {            
            if (line.StartsWith(tag))
            {
                string[] mainDelim = new string[] { ":::" };
                string[] items = line.Split(mainDelim, StringSplitOptions.RemoveEmptyEntries);
                if (items.Length > 1)
                    return items[1].Trim();
            }
            return String.Empty;
        }

        private void StoreReport(SpectrumReport report)
        {
            // Store report in database            
        }

        public void LoadSettings()
        {
            if (!File.Exists(LorakonEnvironment.SettingsFile))
                return;

            // Deserialize settings from file
            using (StreamReader sr = new StreamReader(LorakonEnvironment.SettingsFile))
            {
                XmlSerializer x = new XmlSerializer(settings.GetType());
                settings = x.Deserialize(sr) as Settings;
            }
        }

        private void SaveSettings()
        {
            // Serialize settings to file
            using (StreamWriter sw = new StreamWriter(LorakonEnvironment.SettingsFile))
            {
                XmlSerializer x = new XmlSerializer(settings.GetType());
                x.Serialize(sw, settings);
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            if (MessageBox.Show("Er du sikker på at du vil stoppe mottak av spekterfiler?", "Informasjon", MessageBoxButtons.YesNo) == DialogResult.No)
                return;

            settings.LastShutdownTime = DateTime.Now;
            SaveSettings();

            monitor.Stop();
            timer.Stop();

            Application.Exit();
        }

        private void OnAbout(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Visible = true;
            tabs.SelectedTab = pageAbout;
        }

        private void OnSettings(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Visible = true;
            tabs.SelectedTab = pageSettings;
        }

        private void OnLog(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Visible = true;
            tabs.SelectedTab = pageLog;
        }

        private void FormLorakonSniff_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.ApplicationExitCall || e.CloseReason == CloseReason.TaskManagerClosing)
                return;

            e.Cancel = true;
            Hide();
        }

        private void btnSettingsSave_Click(object sender, EventArgs e)
        {
            if(String.IsNullOrEmpty(tbSettingsWatchDirectory.Text))
            {
                MessageBox.Show("Kan ikke lagre en tom spectrum katalog");
                return;
            }

            if(!Directory.Exists(tbSettingsWatchDirectory.Text))
            {
                MessageBox.Show("Valgt spectrum katalog finnes ikke");
                return;
            }

            if (String.IsNullOrEmpty(tbSettingsConnectionString.Text))
            {
                MessageBox.Show("Kan ikke lagre en tom forbindelses streng");
                return;
            }

            settings.WatchDirectory = tbSettingsWatchDirectory.Text;
            settings.ConnectionString = tbSettingsConnectionString.Text;
            settings.FileFilter = tbSettingsSpectrumFilter.Text;

            SaveSettings();

            monitor = new Monitor(settings, events);
        }

        private void btnLogUpdate_Click(object sender, EventArgs e)
        {            
            lbLog.DataSource = log.GetEntries(dtLogFrom.Value, dtLogTo.Value);            
        }

        private void btnSettingsBrowseWatchDirectory_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog diag = new FolderBrowserDialog();
            if (diag.ShowDialog() != DialogResult.OK)
                return;

            tbSettingsWatchDirectory.Text = diag.SelectedPath;
        }
    }
}
