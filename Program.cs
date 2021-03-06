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
using System.Windows.Forms;

namespace LorakonSniff
{
    static class Program
    {
        [STAThread]
        static void Main()
        {            
            if (SingletonApp.IsRunning())
            {
                MessageBox.Show("Lorakon Sync kjører allerede");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            using (NotifyIcon trayIcon = new NotifyIcon())
            {
                FormLorakonSniff app = new FormLorakonSniff(trayIcon);
                Application.Run(app);
            }            

            SingletonApp.Close();
        }
    }
}
