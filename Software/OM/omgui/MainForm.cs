﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using OmApiNet;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.IO;
using System.Threading;
using System.Xml;

namespace OmGui
{
    public partial class MainForm : Form
    {
        Om om;
        DeviceManager deviceManager;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        private uint queryCancelAutoPlayID;

        //PluginQueue
        Queue<PluginQueueItem> pluginQueueItems = new Queue<PluginQueueItem>();

        protected override void WndProc(ref Message msg)
        {
            base.WndProc(ref msg);
            if (msg.Msg == queryCancelAutoPlayID)
            {
                msg.Result = (IntPtr)1;    // Cancel autoplay
            }
        }

        private bool QueryAdminElevate(bool always)
        {
            WindowsIdentity id = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(id);
            if (principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                return true;
            }

            bool elevate;
            if (always)
            {
                elevate = true;
            }
            else
            {
                DialogResult dr = MessageBox.Show(this, "If you are connecting a large number of devices, \r\nto prevent drive letter exhaustion through remounting, \r\nyou must run this as an administrator.\r\n\r\nAttempt to elevate user level?", "Elevate to Administrator?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (dr == DialogResult.Cancel)
                {
                    Environment.Exit(-1); // Application.Exit(); // this.Close();
                }
                elevate = (dr == DialogResult.Yes);
            }

            if (elevate)
            {
                try
                { 
                    // Start a new instance
                    ProcessStartInfo process = new ProcessStartInfo();
                    process.FileName = Application.ExecutablePath;
                    process.WorkingDirectory = Environment.CurrentDirectory;
                    process.UseShellExecute = true;
                    process.Verb = "runas";
                    Process.Start(process);

                    // Exit this process
                    Environment.Exit(-1); // Application.Exit(); // this.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Error Elevating", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            return false;
        }

        public MainForm(int uac)
        {
            if (uac == 1)
            {
                QueryAdminElevate(false);
            }
            else if (uac == 2)
            {
                QueryAdminElevate(true);
            }
            else if (uac == 3)
            {
                if (!QueryAdminElevate(true))
                {
                    Environment.Exit(-1);
                }
            }

            // Capture any output prior to InitializeComponent
            TextBox initialLog = new TextBox();
            (new TextBoxStreamWriter(initialLog)).SetConsoleOut();

            // Get OM instance
            try
            {
                om = Om.Instance;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error starting OMAPI (" + ex.Message + ")\r\n\r\nCheck the required .dll files are present the correct versions:\r\n\r\nOmApiNet.dll\r\nlibomapi.dll\r\n", "OMAPI Startup Failed", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                Environment.Exit(-1);
            }

            // Initialize the component
            InitializeComponent();
            this.Text += " [V" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString() + "]";

            // Set output redirection
            textBoxLog.Text = initialLog.Text;
            (new TextBoxStreamWriter(textBoxLog)).SetConsoleOut();
            Console.WriteLine("Started.");
            Trace.Listeners.Add(new ConsoleTraceListener());
            //Trace.WriteLine("Trace started.");

            // Cancel Windows auto-play
            queryCancelAutoPlayID = RegisterWindowMessage("QueryCancelAutoPlay");

            // Update status bar
            toolStripStatusLabelMain.Text = "";

            // Device manager
            deviceManager = new DeviceManager(om);
            deviceManager.DeviceUpdate += deviceManager_DeviceUpdate;
            deviceManager.FindInitiallyAttached();

            // Update view options and selection
            View_CheckChanged(null, null);
            listViewDevices_SelectedIndexChanged(null, null);

            // Start refresh timer
            refreshTimer.Enabled = true;

            // Forward mouse wheel events to DataViewer (controls have to have focus for mouse wheel events)
            //this.MouseWheel += (o, e) =>
            //{
            //    Control control = this.GetChildAtPoint(p);
            //    Control lastControl = control;
            //    while (control != null)
            //    {
            //        if (control == dataViewer)
            //        {
            //            Console.WriteLine(e.Delta);
            //            dataViewer.DataViewer_MouseWheel(o, e);
            //        }
            //        lastControl = control;
            //        p.X -= control.Location.X;
            //        p.Y -= control.Location.Y;
            //        control = control.GetChildAtPoint(p);
            //    }
            //};

            filesListView.FullRowSelect = true;
        }


        IDictionary<ushort, ListViewItem> listViewDevices = new Dictionary<ushort, ListViewItem>();
        IDictionary<string, ListViewItem> listViewFiles = new Dictionary<string, ListViewItem>();

        //TS - [P] - Updates the DeviceListViewItem's entry
        //TS - TODO - Work out exactly how this works.
        public void DeviceListViewItemUpdate(System.Windows.Forms.ListViewItem item)
        {
            item.SubItems.Clear();

            //TS - Actually does all the forecolors etc.
            DeviceListViewCreateItem((OmSource) item.Tag, item);

            //TS - [P] - Get and set the category for the list
            //TS - Don't need this anymore because there aren't groups in one list view but a list view each.
            //OmSource.SourceCategory category = source.Category;
            //item.Group = ((DeviceListView)item.ListView).Groups[category.ToString()];
            //if (category == OmSource.SourceCategory.Removed)
            //{
            //    item.ForeColor = System.Drawing.SystemColors.GrayText;
            //    item.Font = new System.Drawing.Font(System.Drawing.SystemFonts.DefaultFont, System.Drawing.FontStyle.Italic);
            //}
            //else if (category == OmSource.SourceCategory.File)
            //{
            //    item.ForeColor = System.Drawing.SystemColors.GrayText;
            //}
            //else
            //{
            //    item.ForeColor = System.Drawing.SystemColors.WindowText;
            //    item.Font = System.Drawing.SystemFonts.DefaultFont;
            //}    
        }

        //Logic for the device list view item.
        private void DeviceListViewCreateItem(OmSource source, ListViewItem item)
        {
            OmDevice device = source as OmDevice;
            OmReader reader = source as OmReader;

            string download = "";
            string battery = "";
            string recording = "";

            //OmSource.SourceCategory category = source.Category;
            //item.Group = ((DeviceListView)item.ListView).Groups[category.ToString()];
            //if (category == OmSource.SourceCategory.Removed)
            //{
            //    item.ForeColor = System.Drawing.SystemColors.GrayText;
            //    item.Font = new System.Drawing.Font(System.Drawing.SystemFonts.DefaultFont, System.Drawing.FontStyle.Italic);
            //}
            //else if (category == OmSource.SourceCategory.File)
            //{
            //    item.ForeColor = System.Drawing.SystemColors.GrayText;
            //}
            //else
            //{
            //    item.ForeColor = System.Drawing.SystemColors.WindowText;
            //    item.Font = System.Drawing.SystemFonts.DefaultFont;
            //}    

            int img = 8;

            if (device != null)
            {
                img = (int)device.LedColor;
                if (img < 0 || img > 8) { img = 8; }

                if (device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_CANCELLED) { download = "Cancelled"; }
                else if (device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_COMPLETE) { download = "Complete"; }
                else if (device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_ERROR) { download = string.Format("Error (0x{0:X})", device.DownloadValue); }
                else if (device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_PROGRESS) { download = "" + device.DownloadValue + "%"; }
                //else if (device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_NONE) { download = "-"; }

                if (device.StartTime >= device.StopTime) { recording = "Stopped"; }
                else if (device.StartTime == DateTime.MinValue && device.StopTime == DateTime.MaxValue) { recording = "Always"; }
                else
                {
                    recording = string.Format("Interval {0:dd/MM/yy HH:mm:ss}-{1:dd/MM/yy HH:mm:ss}", device.StartTime, device.StopTime);
                }

                battery = (device.BatteryLevel < 0) ? "-" : device.BatteryLevel.ToString() + "%";
            }
            else if (reader != null)
            {
                img = 9;

                battery = "-";
                download = "-";
                recording = System.IO.Path.GetFileName(reader.Filename);
            }
            else
            {
                battery = download = recording = null;
            }

            item.ImageIndex = img;

            string deviceText = string.Format("{0:00000}", source.DeviceId);
            string deviceSessionID = (source.SessionId == uint.MinValue) ? "-" : source.SessionId.ToString();
            string deviceBattery = battery;
            string deviceDownload = download;
            string deviceRecording = recording;

            item.UseItemStyleForSubItems = false;


            item.Text = deviceText;
            item.SubItems.Add(deviceSessionID, Color.Black, Color.White, DefaultFont);
            item.SubItems.Add(deviceBattery);
            item.SubItems.Add(deviceDownload);
            item.SubItems.Add(deviceRecording);

            //TS - Colours for each
            //TS - BATTERY
            //Console.WriteLine("battery: " + device.BatteryLevel);
            if (device.BatteryLevel >= 0 && device.BatteryLevel < 33)
            {
                item.SubItems[2].ForeColor = Color.Red;

            }
            else if (device.BatteryLevel >= 33 && device.BatteryLevel < 66)
            {
                item.SubItems[2].ForeColor = Color.Orange;
            }
            else
            {
                item.SubItems[2].ForeColor = Color.Green;
            }

            //TS - DOWNLOAD
            if (device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_CANCELLED || device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_ERROR)
            {
                item.SubItems[3].ForeColor = Color.Red;
            }
            else if (device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_PROGRESS)
            {
                item.SubItems[3].ForeColor = Color.Orange;
            }
            else if (device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_COMPLETE)
            {
                item.SubItems[3].ForeColor = Color.Green;
            }

            //TS - RECORDING
            if (device.IsRecording == OmDevice.RecordStatus.Stopped)
            {
                item.SubItems[4].ForeColor = Color.Red;
            }
            else
            {
                item.SubItems[4].ForeColor = Color.Green;
            }
        }

        //TS - [P] - Hit if the deviceManager needs to update a device (add to the list etc.)
        void deviceManager_DeviceUpdate(object sender, DeviceEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new DeviceManager.DeviceEventHandler(deviceManager_DeviceUpdate), new object[] { sender, e });
                return;
            }

            // Values
            ushort deviceId = e.Device.DeviceId;
            //bool connected = e.Device.Connected;

            ListViewItem item;
            if (!listViewDevices.ContainsKey(deviceId))
            {
                item = new ListViewItem();
                item.Tag = e.Device;
                listViewDevices[deviceId] = item;
                devicesListView.Items.Add(item);
                DeviceListViewItemUpdate(item);
            }
            else
            {
                item = listViewDevices[deviceId];
                UpdateDevice(item);

                devicesListViewUpdateEnabled();
            }

        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            (new AboutBox()).Show(this);
            //string message = Application.ProductName + " " + Application.ProductVersion + "\r\n" + Application.CompanyName;
            //MessageBox.Show(this, message, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void View_CheckChanged(object sender, EventArgs e)
        {
            toolStripMain.Visible = toolbarToolStripMenuItem.Checked;
            statusStripMain.Visible = statusBarToolStripMenuItem.Checked;
            splitContainerLog.Panel2Collapsed = !logToolStripMenuItem.Checked;
            splitContainerPreview.Panel1Collapsed = !previewToolStripMenuItem.Checked;
            splitContainerDevices.Panel2Collapsed = !propertiesToolStripMenuItem.Checked;
        }

        //private void UpdateFile(ListViewItem item)
        //{
        //    OmSource device = (OmSource)item.Tag;
        //    UpdateListViewItem(item);

        //    UpdateEnabled();
        //}

        //TS - [P] - Update the device in terms of the listView
        private void UpdateDevice(ListViewItem item)
        {
            OmSource device = (OmSource)item.Tag;
            DeviceListViewItemUpdate(item);

            //If the list item is for a device
            if (device is OmDevice && !((OmDevice)device).Connected)
            {
                listViewDevices.Remove(device.DeviceId);
                devicesListView.Items.Remove(item);
            }

            //UpdateEnabled();
        }

        //TS - Update file in listView
        private void UpdateFile(ListViewItem item, string path)
        {
            string fileName = System.IO.Path.GetFileNameWithoutExtension(path);

            System.IO.FileInfo info = new System.IO.FileInfo(path);
            string fileSize = info.Length.ToString();
            string dateModified = info.CreationTime.ToString("dd/MM/yy HH:mm:ss");

            item.SubItems.Clear();

            item.SubItems.Add(path);
            item.SubItems.Add(fileSize);
            item.SubItems.Add(dateModified);

            item.Text = fileName;
        }

        int refreshIndex = 0;
        private void refreshTimer_Tick(object sender, EventArgs e)
        {
            if (refreshIndex >= devicesListView.Items.Count) { refreshIndex = 0; }
            if (refreshIndex < devicesListView.Items.Count)
            {
                ListViewItem item = devicesListView.Items[refreshIndex];
                OmSource device = (OmSource)item.Tag;
                if (device is OmDevice && ((OmDevice)device).Update())
                {
                    UpdateDevice(item);
                }
            }
            refreshIndex++;
        }

        public static string GetPath(string template)
        {
            template = template.Replace("{MyDocuments}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            template = template.Replace("{Desktop}", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            template = template.Replace("{LocalApplicationData}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            template = template.Replace("{ApplicationData}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            template = template.Replace("{CommonApplicationData}", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            if (!template.EndsWith("\\")) { template += "\\"; }
            return template;
        }


        private void toolStripButtonDownload_Click(object sender, EventArgs e)
        {
            //TS - Recording how many are downloading from selected items to display in prompt.
            int numDevicesDownloaded = 0;
            int numDevicesNotDownloaded = 0;
            List<string> fileNamesDownloaded = new List<string>();

            foreach (ListViewItem i in devicesListView.SelectedItems)
            {
                OmDevice device = (OmDevice) i.Tag;
                if (device != null && !device.IsDownloading && device.IsRecording == OmDevice.RecordStatus.Stopped && device.HasData)
                {
                    bool ok = true;
                    string folder = GetPath(OmGui.Properties.Settings.Default.CurrentWorkingFolder);
                    System.IO.Directory.CreateDirectory(folder);
                    string prefix = folder + string.Format("{0:00000}_{1:0000000000}", device.DeviceId, device.SessionId);
                    string finalFilename = prefix + ".cwa";
                    string downloadFilename = finalFilename;

                    downloadFilename += ".part";
                    if (System.IO.File.Exists(downloadFilename))
                    {
                        DialogResult dr = MessageBox.Show(this, "Download file already exists:\r\n\r\n    " + downloadFilename + "\r\n\r\nOverwrite existing file?", "Overwrite File?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                        if (dr != DialogResult.OK)
                        {
                            ok = false;

                            //TS - Device not downloaded because user clicked cancel on overwrite.
                            numDevicesNotDownloaded++;
                        }
                        else { System.IO.File.Delete(downloadFilename); }


                    }

                    if (ok && System.IO.File.Exists(finalFilename))
                    {
                        DialogResult dr = MessageBox.Show(this, "File already exists:\r\n\r\n    " + downloadFilename + "\r\n\r\nOverwrite existing file?", "Overwrite File?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                        if (dr != DialogResult.OK)
                        {
                            ok = false;

                            //TS - Device not downloaded because user clicked cancel on overwrite.
                            numDevicesNotDownloaded++;
                        }
                        else { System.IO.File.Delete(finalFilename); }
                    }

                    //Clicked OK and want to download.
                    if (ok)
                    {
                        device.BeginDownloading(downloadFilename, finalFilename);

                        //TS - Device downloaded because user clicked OK.
                        numDevicesDownloaded++;
                        fileNamesDownloaded.Add(finalFilename);
                    }
                }
                else
                {
                    //Couldn't download so hasn't downloaded
                    numDevicesNotDownloaded++;
                }
            }

            //TS - If multiple devices selected then show which downloaded and which didnt.
            if (devicesListView.SelectedItems.Count > 0)
            {
                string message = numDevicesDownloaded + " devices downloaded from a selection of " + (int) (numDevicesNotDownloaded + numDevicesDownloaded) + " devices." + "\r\nFiles:";

                foreach (string fileName in fileNamesDownloaded)
                {
                    message += "\r\n" + fileName;
                }

                MessageBox.Show(message, "Download Status", MessageBoxButtons.OK);
            }
        }

        //TS - [P] - Updates toolstrip buttons based on what has been selected in devicesListView
        public OmSource[] devicesListViewUpdateEnabled()
        {
            List<OmSource> selected = new List<OmSource>();

            DevicesResetToolStripButtons();

            //TS - If a single device has been selected
            if (devicesListView.SelectedItems.Count == 1)
            {
                bool downloading = false, hasData = false, isRecording = false, isSetupForRecord = false;

                OmDevice device = (OmDevice)devicesListView.SelectedItems[0].Tag;

                //Is Downloading
                if (((OmDevice)device).DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_PROGRESS)
                {
                    downloading = true;
                }

                //Has Data
                if (((OmDevice)device).HasData)
                {
                    hasData = true;
                }

                if (device.StartTime > DateTime.Now && (device.StartTime != DateTime.MaxValue)) { isSetupForRecord = true; };

                //TS - [P] - Taken from somewhere else, check if device is recording or not.
                if (device.StartTime >= device.StopTime) { isRecording = false; }
                else if (device.StartTime == DateTime.MinValue && device.StopTime == DateTime.MaxValue) { isRecording = true; }

                //Can Download/Clear
                toolStripButtonDownload.Enabled = !downloading && hasData && !isRecording && !isSetupForRecord; //If has data and not downloading
                toolStripButtonClear.Enabled = !downloading && hasData && !isRecording && !isSetupForRecord; //If not downloading but have data then can clear

                //Can Cancel Download
                toolStripButtonCancelDownload.Enabled = downloading; //If downloading then can cancel download

                //Can sync time
                //toolStripButtonSyncTime.Enabled = true; //If selected any then can sync time //TS - TODO - Ask Dan can this be done if downloading/recording etc.

                toolStripButtonStopRecording.Enabled = isRecording || isSetupForRecord; //If single and recording/setup for record then can stop recording

                toolStripButtonInterval.Enabled = !downloading && (!isRecording && !isSetupForRecord); //If any selected and not downloading then can set up interval.

                devicesToolStripButtonIdentify.Enabled = true;

                selected.Add(device);
            }
            //TS - Multiple rows logic - Only indicates of some or all are doing something so the buttons will actually have to do some logic as it iterates through each selected.
            else if (devicesListView.SelectedItems.Count > 1)
            {
                bool allDownloading = true, someDownloading = false, allHaveData = true, someHaveData = false, allRecording = true, someRecording = false, allSetupForRecord = true, someSetupForRecord = false;

                foreach (ListViewItem item in devicesListView.SelectedItems)
                {
                    OmDevice device = (OmDevice)item.Tag;

                    //DOWNLOADING
                    //If it is downloading then set downloading.
                    if (device.DownloadStatus == OmApi.OM_DOWNLOAD_STATUS.OM_DOWNLOAD_PROGRESS)
                    {
                        someDownloading = true;
                    }
                    //If it isn't downloading and allDownloading is true then set it false.
                    else if(allDownloading)
                    {
                        allDownloading = false;
                    }; 

                    //HASDATA
                    //If it has data then some have data.
                    if (device.HasData)
                    {
                        someHaveData = true;
                    }
                    //If don't have data then allHaveData set false.
                    else if (allHaveData)
                    {
                        allHaveData = false;
                    }

                    //ALL RECORDING
                    if (device.StartTime < device.StopTime) 
                    {
                        someRecording = true;
                    }
                    else if (allRecording) 
                    { 
                        allRecording = false; 
                    }

                    //SETUP FOR RECORD
                    if (device.StartTime > DateTime.Now && (device.StartTime != DateTime.MaxValue))
                    { 
                        someSetupForRecord = true; 
                    }
                    else if (allSetupForRecord)
                    {
                        allSetupForRecord = false;
                    }

                    selected.Add(device);
                }

                //TS - ToolStrip Logic for multiple

                //Can Download/Clear
                toolStripButtonDownload.Enabled = someHaveData && !allRecording; //If has data and not downloading
                toolStripButtonClear.Enabled = someDownloading || allDownloading; //If not downloading but have data then can clear

                //Can Cancel Download
                toolStripButtonCancelDownload.Enabled = someDownloading || allDownloading; //If downloading then can cancel download

                //Can sync time
                //toolStripButtonSyncTime.Enabled = true; //If selected any then can sync time //TS - TODO - Ask Dan can this be done if downloading/recording etc.

                toolStripButtonStopRecording.Enabled = someRecording || allRecording || someSetupForRecord || allSetupForRecord; //If single and recording/setup for record then can stop recording

                //toolStripButtonRecord.Enabled = !allDownloading && !allRecording && !allSetupForRecord; //If any selected and not downloading then can record.
                toolStripButtonInterval.Enabled = !allDownloading && !allRecording && !allSetupForRecord; //If any selected and not downloading then can set up interval.
                //toolStripButtonSessionId.Enabled = !allDownloading && !allRecording && !allSetupForRecord; //If multiple or single and none are downloading then can set session ID.

                devicesToolStripButtonIdentify.Enabled = true;
            }

            //TS - DEBUG - Record always lit
            //toolStripButtonInterval.Enabled = true;

            return selected.ToArray();
        }

        private void DevicesResetToolStripButtons()
        {
            toolStripButtonDownload.Enabled = false;
            toolStripButtonClear.Enabled = false;

            toolStripButtonCancelDownload.Enabled = false;

            //toolStripButtonSyncTime.Enabled = false;

            toolStripButtonStopRecording.Enabled = false;
            //toolStripButtonRecord.Enabled = false;
            toolStripButtonInterval.Enabled = false;
            //toolStripButtonSessionId.Enabled = false;

            devicesToolStripButtonIdentify.Enabled = false;
        }

        
        private void listViewDevices_SelectedIndexChanged(object sender, EventArgs e)
        {
            //TS - [P] - Get the list of selected sources and in turn change the toolbar enabled/disabled.
            OmSource[] selected = devicesListViewUpdateEnabled();

            //TS - Deselect any files from listview if devices selected
            if (selected.Length > 0)
            {
                filesListView.SelectedItems.Clear();
            }

            //TS - [P] - TODO - Ask Dan why we have a property window, it isn't used yet.
            propertyGridDevice.SelectedObjects = selected;

            //TS - [P] - If there is one device selected then open it in the dataViewer
            if (selected.Length == 1 && selected[0] is OmDevice && ((OmDevice)selected[0]).Connected)
            {
                dataViewer.Open(selected[0].DeviceId);
            }
            else
            {
                dataViewer.Close();
            }

        }

        private void listViewDevices_ItemActivate(object sender, EventArgs e)
        {
            //            MessageBox.Show("");
        }


        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            // Save properties
            Properties.Settings.Default.Save();
        }

        private void optionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OptionsDialog optionsDialog = new OptionsDialog();
            DialogResult dr = optionsDialog.ShowDialog(this);

            //PropertyEditor propertyEditor = new PropertyEditor("Options", OmGui.Properties.Settings.Default, true);
            //DialogResult dr = propertyEditor.ShowDialog(this);

            if (dr == DialogResult.OK)
            {
                OmGui.Properties.Settings.Default.DefaultWorkingFolder = optionsDialog.DefaultFolderName;
                OmGui.Properties.Settings.Default.CurrentPluginFolder = optionsDialog.DefaultPluginName;

                Console.WriteLine(Properties.Settings.Default.DefaultWorkingFolder);
                Console.WriteLine(Properties.Settings.Default.CurrentPluginFolder);
                OmGui.Properties.Settings.Default.Save();
            }
            else
            {
                OmGui.Properties.Settings.Default.Reload();
            }
        }


        //public void DataFileRemove(string file)
        //{
        //    if (listViewFiles.ContainsKey(file))
        //    {
        //        devicesListView.Items.Remove(listViewFiles[file]);
        //        listViewFiles.Remove(file);
        //    }
        //}

        //TS - [P] - Old add function for joint listview, now seperate
        //public void DataFileAdd(string file)
        //{
        //    OmSource device = OmReader.Open(file);
        //    if (device != null)
        //    {
        //        ListViewItem listViewItem = new ListViewItem();
        //        listViewItem.Tag = device;
        //        devicesListView.Items.Add(listViewItem);
        //        listViewFiles[file] = listViewItem;
        //        UpdateDevice(listViewItem);
        //    }
        //}

        public void FileListViewRemove(string file, bool delete)
        {
            if (listViewFiles.ContainsKey(file))
            {
                filesListView.Items.Remove(listViewFiles[file]);
                listViewFiles.Remove(file);

                if (delete)
                {
                    try
                    {
                        System.IO.File.Delete(file);
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(e.Message);
                    }
                }
            }
        }

        public void FileListViewAdd(string file)
        {
            OmReader device = null;
            try
            {
                device = OmReader.Open(file);
            } catch (Exception) { Console.Error.WriteLine("ERROR: Problem reading file: " + file); }

            if (device != null)
            {
                ListViewItem item = new ListViewItem();
                item.Tag = device;

                UpdateFile(item, file);

                filesListView.Items.Add(item);
                listViewFiles[file] = item;

                device.Close();
            }
        }

        private void fileSystemWatcher_Renamed(object sender, System.IO.RenamedEventArgs e)
        {
            FileListViewRemove(e.OldFullPath, false);
            if (e.FullPath.EndsWith(fileSystemWatcher.Filter.Substring(1), StringComparison.InvariantCultureIgnoreCase))
            {
                FileListViewAdd(e.FullPath);
            }
        }

        private void fileSystemWatcher_Created(object sender, System.IO.FileSystemEventArgs e)
        {
            FileListViewAdd(e.FullPath);
        }

        private void fileSystemWatcher_Deleted(object sender, System.IO.FileSystemEventArgs e)
        {
            FileListViewRemove(e.FullPath, false);
        }

        private void selectAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (ListViewItem item in devicesListView.Items)
            {
                item.Selected = true;
            }
        }

        private void toolStripButtonCancelDownload_Click(object sender, EventArgs e)
        {
            //TS - Recording how many are canceling from selected items to display in prompt.
            int devicesCancelling = 0;

            Cursor.Current = Cursors.WaitCursor;

            foreach (ListViewItem i in devicesListView.SelectedItems)
            {
                OmSource device = (OmSource)i.Tag;
                if (device is OmDevice && ((OmDevice)device).IsDownloading)
                {
                    ((OmDevice)device).CancelDownload();

                    devicesCancelling++;
                }
            }

            Cursor.Current = Cursors.Default;

            //TS - Show messagebox as there are multiple.
            if(devicesListView.SelectedItems.Count > 1)
            {
                MessageBox.Show(devicesCancelling + " devices cancelled downloading from a selection of " + devicesListView.SelectedItems.Count + " devices.", "Cancel Status", MessageBoxButtons.OK);
            }
        }

        public bool EnsureNoSelectedDownloading()
        {
            int downloading = 0, total = 0;
            foreach (ListViewItem i in devicesListView.SelectedItems)
            {
                OmSource device = (OmSource)i.Tag;
                if (device is OmDevice && ((OmDevice)device).IsDownloading) { downloading++; }
                total++;
            }
            if (downloading > 0)
            {
                MessageBox.Show(this, "Download in progress for " + downloading + " (of " + total + " selected) device(s) -- cannot change configuration of these devices until download complete or cancelled.", "Download in progress", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }

        private void toolStripButtonStop_Click(object sender, EventArgs e)
        {
            //TS - Record which devices stopped recording out of the selected.
            int devicesStoppedRecording = 0;

            //TS - [P] - Removed this check as can now just give prompt box.
            //if (EnsureNoSelectedDownloading())
            //{
                List<string> fails = new List<string>();
                dataViewer.CancelPreview();
                Cursor.Current = Cursors.WaitCursor;
                foreach (ListViewItem i in devicesListView.SelectedItems)
                {
                    OmSource device = (OmSource)i.Tag;
                    if (device is OmDevice && !((OmDevice)device).IsDownloading && ((OmDevice) device).IsRecording != OmDevice.RecordStatus.Stopped)
                    {
                        if (!((OmDevice)device).NeverRecord())
                        {
                            fails.Add(device.DeviceId.ToString());
                        }
                        else
                        {
                            //TS - Record device stopped recording.
                            devicesStoppedRecording++;
                        }
                    }
                }
                Cursor.Current = Cursors.Default;
                if (fails.Count > 0) { MessageBox.Show(this, "Failed operation on " + fails.Count + " device(s):\r\n" + string.Join("; ", fails.ToArray()), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1); }
            //}

                //TS - If multiple devices then show which stopped and which didn't.
                if (devicesListView.SelectedItems.Count > 1)
                {
                    MessageBox.Show(devicesStoppedRecording + " devices stopped recording from a selection of " + devicesListView.SelectedItems.Count + " devices.", "Stop Record Status", MessageBoxButtons.OK);
                }

            devicesListViewUpdateEnabled();
        }

        //private void toolStripButtonRecord_Click(object sender, EventArgs e)
        //{
        //    //TS - [P] - If nothing is downloading then cancel preview of dataViewer and turn on AlwaysRecord() for each device selected.
        //    if (EnsureNoSelectedDownloading())
        //    {
        //        List<string> fails = new List<string>();
        //        dataViewer.CancelPreview();
        //        Cursor.Current = Cursors.WaitCursor;
        //        foreach (ListViewItem i in devicesListView.SelectedItems)
        //        {
        //            OmSource device = (OmSource)i.Tag;
        //            if (device is OmDevice && !((OmDevice)device).IsDownloading)
        //            {
        //                if (!((OmDevice)device).AlwaysRecord())
        //                    fails.Add(device.DeviceId.ToString());
        //            }
        //        }
        //        Cursor.Current = Cursors.Default;
        //        if (fails.Count > 0) { MessageBox.Show(this, "Failed operation on " + fails.Count + " device(s):\r\n" + string.Join("; ", fails.ToArray()), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1); }
        //    }

        //    devicesListViewUpdateEnabled();
        //}

        private void toolStripButtonInterval_Click(object sender, EventArgs e)
        {
            //if (EnsureNoSelectedDownloading())
            //{
                List<string> fails = new List<string>();

                DateTime start = DateTime.MinValue;
                DateTime stop = DateTime.MaxValue;

                OmDevice device = (OmDevice)devicesListView.SelectedItems[0].Tag;

                DateRangeForm rangeForm = new DateRangeForm("Session Range", "Session Range", device);
                DialogResult dr = rangeForm.ShowDialog();
                start = rangeForm.FromDate;
                stop = rangeForm.UntilDate; //TS - TODO - stop needs to be got from dialog.

                if (dr == System.Windows.Forms.DialogResult.OK)
                {
                    dataViewer.CancelPreview();
                    Cursor.Current = Cursors.WaitCursor;

                    //Don't want it to be able to work on multiple do we because of metadata??
                    //foreach (ListViewItem i in devicesListView.SelectedItems)
                    //{                        
                        if (device is OmDevice && !((OmDevice)device).IsDownloading)
                        {
                            //Set Date
                            if (rangeForm.Always)
                            {
                                //TS - Taken from Record button
                                if (!((OmDevice)device).AlwaysRecord())
                                    fails.Add(device.DeviceId.ToString());
                            }
                            else
                            {
                                if (!((OmDevice)device).SetInterval(start, stop))
                                    fails.Add(device.DeviceId.ToString());
                            }

                            //Set SessionID
                            if (!((OmDevice)device).SetSessionId((uint)rangeForm.SessionID))
                                fails.Add(device.DeviceId.ToString());

                            //TODO - Set Metadata
                            
                        }
                    }
                    Cursor.Current = Cursors.Default;

                //if (fails.Count > 0) { MessageBox.Show(this, "Failed operation on " + fails.Count + " device(s):\r\n" + string.Join("; ", fails.ToArray()), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1); }
            //}

                //TS - If more than 1 device selected then show which could and couldn't be set up.
                //if (devicesListView.SelectedItems.Count > 1)
                //{
                //    MessageBox.Show(devicesCanRecord + " devices setup record from a selection of " + devicesListView.SelectedItems.Count + " devices.", "Record Status", MessageBoxButtons.OK);
                //}

            //Do Sync Time
            if (rangeForm.SyncTime != DateRangeForm.SyncTimeType.None)
            {
                List<string> fails2 = new List<string>();
                Cursor.Current = Cursors.WaitCursor;
                //foreach (ListViewItem i in devicesListView.SelectedItems)
                //{
                //    OmSource device = (OmSource)i.Tag;
                    if (device is OmDevice && ((OmDevice)device).Connected)
                    {
                        if (!((OmDevice)device).SyncTime((int) rangeForm.SyncTime))
                            fails2.Add(device.DeviceId.ToString());
                    }
                //}
                Cursor.Current = Cursors.Default;
                if (fails2.Count > 0) { MessageBox.Show(this, "Failed operation on " + fails2.Count + " device(s):\r\n" + string.Join("; ", fails2.ToArray()), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1); }
            }

            devicesListViewUpdateEnabled();
        }

        private void toolStripButtonSessionId_Click(object sender, EventArgs e)
        {
            if (EnsureNoSelectedDownloading())
            {
                InputBox inputBox = new InputBox("Session ID", "Session ID", "0");
                DialogResult dr = inputBox.ShowDialog();
                if (dr == System.Windows.Forms.DialogResult.OK)
                {
                    List<string> fails = new List<string>();
                    uint value = 0;
                    if (!uint.TryParse(inputBox.Value, out value)) { return; }

                    dataViewer.CancelPreview();
                    Cursor.Current = Cursors.WaitCursor;
                    foreach (ListViewItem i in devicesListView.SelectedItems)
                    {
                        OmSource device = (OmSource)i.Tag;
                        if (device is OmDevice && !((OmDevice)device).IsDownloading)
                        {
                            if (!((OmDevice)device).SetSessionId(value))
                                fails.Add(device.DeviceId.ToString());
                        }
                    }
                    Cursor.Current = Cursors.Default;
                    if (fails.Count > 0) { MessageBox.Show(this, "Failed operation on " + fails.Count + " device(s):\r\n" + string.Join("; ", fails.ToArray()), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1); }
                }
            }
        }

        private void toolStripButtonClear_Click(object sender, EventArgs e)
        {
            //TS - Record which are cleared and stopped if multiple selected.
//            int devicesClearing = 0;

            if (EnsureNoSelectedDownloading())
            {
                DialogResult dr = MessageBox.Show(this, "Clear " + devicesListView.SelectedItems.Count + " device(s)?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (dr == System.Windows.Forms.DialogResult.OK)
                {
                    //Cursor.Current = Cursors.WaitCursor;

                    dataViewer.CancelPreview();
                    dataViewer.Reader = null;
                    dataViewer.Close();

                    List<OmDevice> devices = new List<OmDevice>();
                    foreach (ListViewItem i in devicesListView.SelectedItems)
                    {
                        OmSource device = (OmSource)i.Tag;
                        if (device is OmDevice && !((OmDevice)device).IsDownloading)
                        {
                            devices.Add((OmDevice)device);
                        }
                    }

                    BackgroundWorker clearBackgroundWorker = new BackgroundWorker();
                    clearBackgroundWorker.WorkerReportsProgress = true;
                    clearBackgroundWorker.WorkerSupportsCancellation = true;
                    clearBackgroundWorker.DoWork += (s, ea) =>
                    {
                        List<string> fails = new List<string>();
                        int i = 0;
                        foreach (OmDevice device in devices)
                        {
                            clearBackgroundWorker.ReportProgress(-1, "Clearing device " + (i + 1) + " of " + devices.Count + ".");
                            if (!((OmDevice)device).Clear())
                                fails.Add(device.DeviceId.ToString());
                            i++;
                        }
                        clearBackgroundWorker.ReportProgress(100, "Done");
                        if (fails.Count > 0)
                        {
                            this.Invoke(new Action(() =>
                                MessageBox.Show(this, "Failed operation on " + fails.Count + " device(s):\r\n" + string.Join("; ", fails.ToArray()), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1)
                            ));
                        }
                    };

                    ProgressBox progressBox = new ProgressBox("Clearing", "Clearing devices...", clearBackgroundWorker);
                    progressBox.ShowDialog(this);

                    //Cursor.Current = Cursors.Default;
                }
            }
        }

        private void openDownloadFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // "explorer.exe /select, <filename>"
            System.Diagnostics.Process.Start(GetPath(OmGui.Properties.Settings.Default.CurrentWorkingFolder));
        }

        private void ExportDataConstruct(OmSource.SourceCategory category)
        {
            float blockStart = -1;
            float blockCount = -1;

            // Selection
            if (dataViewer != null)
            {
                if (dataViewer.HasSelection)
                {
                    blockStart = dataViewer.SelectionBeginBlock + dataViewer.OffsetBlocks;
                    blockCount = dataViewer.SelectionEndBlock - dataViewer.SelectionBeginBlock;
                }
            }

            List<string> files = new List<string>();

            //TS - Record how many can be exported. (Little different to the other buttons because this method is used for both files and devices.
            int devicesSelected = -1; //-1 means files.
            if(devicesListView.SelectedItems.Count > 0)
            {
                devicesSelected = devicesListView.SelectedItems.Count;
            }

            //TS - TODO - Will need to have array of devices for batch export.
            if (devicesListView.SelectedItems.Count > 0)
            {
                foreach (ListViewItem item in devicesListView.SelectedItems)
                {
                    //TS - Don't want to try and export from a device that is running.
                    OmDevice device = (OmDevice)item.Tag;

                    //TS - If the device isn't recording or downloading then we can export.
                    if (device.IsRecording == OmDevice.RecordStatus.Stopped && !device.IsDownloading)
                    {
                        files.Add(device.Filename);
                    }
                }
            }
            else if(filesListView.SelectedItems.Count > 0)
            {
                foreach (ListViewItem item in filesListView.SelectedItems)
                {
                    OmReader reader = (OmReader)item.Tag;
                    files.Add(reader.Filename);
                }
            }

            //TS - [P] - Old now -> When files and devices were both in same list view.
            //foreach (ListViewItem i in devicesListView.SelectedItems)
            //{
            //    OmSource device = (OmSource)i.Tag;
            //    if (!(device is OmDevice) || !((OmDevice)device).Connected)
            //    {
            //        if (device is OmReader)
            //        {
            //            files.Add(((OmReader)device).Filename);
            //            numFiles++;
            //        }
            //    }
            //    else
            //    {
            //        if (device is OmDevice)
            //        {
            //            files.Add(((OmDevice)device).Filename);
            //            numDevices++;
            //        }
            //    }
            //}

            //Export files
            //If we are chosing from the dataViewer then we only have one else we can have many.
            if (blockStart > -1 && blockCount > -1)
                ExportData(files[0], blockStart, blockCount);
            else
                ExportData(files, devicesSelected);
        }

        private void ExportData(string fileName, float blockStart, float blockCount)
        {
            string folder = GetPath(Properties.Settings.Default.CurrentWorkingFolder);
            ExportForm exportForm = new ExportForm(fileName, folder, blockStart, blockCount);
            DialogResult result = exportForm.ShowDialog(this);
        }

        //Exporting many.
        private void ExportData(List<string> files, int numDevicesSelected)
        {
            string folder = GetPath(Properties.Settings.Default.CurrentWorkingFolder);

            ExportForm exportForm;

            foreach (string fileName in files)
            {
                exportForm = new ExportForm(fileName, folder, -1, -1);
                exportForm.ShowDialog();
            }

            //If 'numDevicesSelected' > -1 then we are dealing with devices otherwise we are dealing with files.
            if (numDevicesSelected > 1)
            {
                //TS - Display messagebox of which exported from those selected.
                MessageBox.Show(files.Count + " device data exported from a selection of " + numDevicesSelected + " devices.", "Export Status", MessageBoxButtons.OK);
            }
        }

        #region Working Folder Paradigm

        //TS - Properties
        public string CurrentWorkingFolder { get; set; }
        
        //TS - Fields
        public bool inWorkingFolder = false;
        string defaultTitleText = "Open Movement (Beta Testing Version)";

        private void workingFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog fbd = new FolderBrowserDialog();
            fbd.ShowNewFolderButton = true;
            fbd.SelectedPath = Properties.Settings.Default.CurrentWorkingFolder;
            fbd.Description = "Choose or Create New Working Folder";

            DialogResult dr = fbd.ShowDialog();

            if (dr == System.Windows.Forms.DialogResult.OK)
            {
                Properties.Settings.Default.CurrentWorkingFolder = fbd.SelectedPath;
            }

            LoadWorkingFolder(false);
        }

        private void LoadWorkingFolder(bool defaultFolder)
        {
            //If first time then change to my documents
            if (Properties.Settings.Default.DefaultWorkingFolder.Equals("C:\\"))
            {
                Properties.Settings.Default.DefaultWorkingFolder = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            }

            if (defaultFolder)
                Properties.Settings.Default.CurrentWorkingFolder = Properties.Settings.Default.DefaultWorkingFolder;

            //Load Plugin
            //Properties.Settings.Default.CurrentPluginFolder = Properties.Settings.Default.CurrentWorkingFolder;

            inWorkingFolder = true;

            Text = defaultTitleText + " - " + Properties.Settings.Default.CurrentWorkingFolder;

            //Remove files already in list if changed folder
            filesListView.Items.Clear();
            listViewFiles.Clear();

            //Find files
            try
            {
                string folder = GetPath(OmGui.Properties.Settings.Default.CurrentWorkingFolder);

                if (!System.IO.Directory.Exists(folder))
                {
                    System.IO.Directory.CreateDirectory(folder);
                }

                fileSystemWatcher.Path = folder;

                string[] filePaths = System.IO.Directory.GetFiles(folder, fileSystemWatcher.Filter, System.IO.SearchOption.TopDirectoryOnly);
                foreach (string f in filePaths)
                {
                    FileListViewAdd(f);
                }
            }
            catch (Exception e) 
            {
                Console.WriteLine(e.Message); 
            }

            //Add files in project to list.
            //if (downloadedFiles.Length > 0)
            //{
            //    foreach (string fileName in downloadedFiles)
            //    {
            //        FileListViewAdd(Properties.Settings.Default.CurrentWorkingFolder + "\\" + fileName);
            //    }
            //}

            //Set watcher path
            fileSystemWatcher.Path = OmGui.Properties.Settings.Default.CurrentWorkingFolder;
        }

        private void UnloadWorkingFolder()
        {
            Properties.Settings.Default.Reset();

            Text = defaultTitleText;

            inWorkingFolder = false;
        }

        #endregion

        //TS - TODO - This is hard-coded at the moment for testing purposes.
        private const string PLUGIN_PROFILE_FILE = "profile.xml";

        private void MainForm_Load(object sender, EventArgs e)
        {
            FilesResetToolStripButtons();

            //TS - Working Folder logic
            //If current plugin folder is empty then make it My Documents.
            if (Properties.Settings.Default.CurrentPluginFolder == "")
                Properties.Settings.Default.CurrentPluginFolder = GetPath("C:\\OM\\DefaultPlugins\\");

            //If there is no default folder then make it My Documents
            if (Properties.Settings.Default.DefaultWorkingFolder == "")
                Properties.Settings.Default.DefaultWorkingFolder = GetPath("{MyDocuments}");

            Console.WriteLine("Default: " + Properties.Settings.Default.DefaultWorkingFolder);
            Console.WriteLine("Current: " + Properties.Settings.Default.CurrentWorkingFolder);
            Console.WriteLine("Current Plugin: " + Properties.Settings.Default.CurrentPluginFolder);

            if (Properties.Settings.Default.CurrentWorkingFolder != "" && Properties.Settings.Default.CurrentWorkingFolder != Properties.Settings.Default.DefaultWorkingFolder)
            {
                DialogResult dr = MessageBox.Show("Do you want to load the last opened Working Folder?\r\n\r\nLast Folder:\r\n" + Properties.Settings.Default.CurrentWorkingFolder, "Open Last Working Folder?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (dr == System.Windows.Forms.DialogResult.Yes)
                {
                    LoadWorkingFolder(false);
                }
                else
                {
                    LoadWorkingFolder(true);
                }
            }
            else
            {
                LoadWorkingFolder(true);
            }

            //Add Profile Plugins
            AddProfilePluginsToToolStrip();
        }

        private void AddProfilePluginsToToolStrip()
        {
            //TS - Add tool strip buttons for "default" profiles
            try
            {
                StreamReader pluginProfile = new StreamReader(Properties.Settings.Default.CurrentWorkingFolder + Path.DirectorySeparatorChar + PLUGIN_PROFILE_FILE);
                string pluginProfileAsString = pluginProfile.ReadToEnd();

                //Parse
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(pluginProfileAsString);
                XmlNodeList items = xmlDoc.SelectNodes("Profile/Plugin");

                //Used to add seperator.
                bool isFirst = true;

                //Each <Plugin>
                foreach (XmlNode node in items)
                {
                    string name = "";
                    string files = "";

                    //Each < /> inside <Plugin>
                    foreach (XmlNode childNode in node.ChildNodes)
                    {
                        switch (childNode.Name)
                        {
                            case "name":
                                name = childNode.InnerText;
                                break;
                            case "files":
                                files = childNode.InnerText;
                                break;
                        }
                    }

                    FileInfo[] pluginInfo = getPluginInfo(files);

                    if (pluginInfo != null)
                    {
                        if (pluginInfo[0] != null && pluginInfo[1] != null && pluginInfo[2] != null)
                        {
                            Plugin plugin = new Plugin(pluginInfo[0], pluginInfo[1], pluginInfo[2]);

                            ToolStripButton tsb = new ToolStripButton();
                            tsb.Text = name;
                            tsb.Tag = plugin;

                            if(plugin.Icon != null)
                                tsb.Image = plugin.Icon;

                            tsb.Click += new EventHandler(tsb_Click);

                            if (isFirst)
                            {
                                toolStripFiles.Items.Add(new ToolStripSeparator());
                                isFirst = false;
                            }

                            toolStripFiles.Items.Add(tsb);
                            toolStripFiles.Items[toolStripFiles.Items.Count - 1].Enabled = false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: " + e.Message);
            }
        }

        //Click event handler for the tool strip default plugins that comes from the profile file...
        void tsb_Click(object sender, EventArgs e)
        {
            ToolStripButton tsb = sender as ToolStripButton;

            if (tsb != null && tsb.Tag != null)
            {
                if(filesListView.SelectedItems.Count > 0)
                {
                    OmReader reader = (OmReader) filesListView.SelectedItems[0].Tag;

                    string CWAFilename = reader.Filename;
                    Plugin p = (Plugin) tsb.Tag;

                    RunPluginForm rpf = new RunPluginForm(p, CWAFilename);
                    rpf.ShowDialog();

                    if (rpf.DialogResult == System.Windows.Forms.DialogResult.OK)
                    {
                        //if the plugin has an output file    
                        RunProcess(p, rpf.ParameterString, rpf.OriginalOutputName, reader.Filename);
                    }
                }
                else
                {
                    MessageBox.Show("Please choose a file to run a plugin on.");
                }
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            //TS - If project exists and isn't the default then ask if its properties want to be saved to be automatically opened next time.
            if (Properties.Settings.Default.CurrentWorkingFolder != "" && Properties.Settings.Default.CurrentWorkingFolder != Properties.Settings.Default.DefaultWorkingFolder)
            {
                DialogResult dr = MessageBox.Show("The current working folder isn't the default.\r\n\r\nLoad up into this Working Folder next time?", "Keep Working Folder Properties?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (dr == System.Windows.Forms.DialogResult.Yes)
                {
                    //Save properties for next opening.
                    Properties.Settings.Default.Save();
                }
                else if (dr == System.Windows.Forms.DialogResult.No)
                {
                    Properties.Settings.Default.Reset();
                }
                else
                {
                    e.Cancel = true;
                    return;
                }
            }
            Om.Instance.Dispose();
        }

        private void filesListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            //TS - If a file has been selected then deselect all rows of Devices list.
            if (filesListView.SelectedItems.Count > 0)
            {
                //TS - TODO - The text goes from Red to Black...
                devicesListView.SelectedItems.Clear();

                //Enable plugins
                for (int i = 0; i < toolStripFiles.Items.Count; i++)
                {
                    toolStripFiles.Items[i].Enabled = true;
                }
            }
            else
            {
                FilesResetToolStripButtons();
            }

            //Open DataViewer
            if(filesListView.SelectedItems.Count == 1)
            {
                OmReader reader = (OmReader)filesListView.SelectedItems[0].Tag;
                dataViewer.Open(reader.Filename);
            }
        }

        private void FilesResetToolStripButtons()
        {
            //Disable buttons
            for (int i = 0; i < toolStripFiles.Items.Count; i++)
            {
                toolStripFiles.Items[i].Enabled = false;
            }
        }

        private void DeleteFilesToolStripButton_Click(object sender, EventArgs e)
        {
            DialogResult dr = MessageBox.Show("Are you sure?", "Delete Data File", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button2);

            if (dr == System.Windows.Forms.DialogResult.Yes)
            {
                foreach (ListViewItem item in filesListView.SelectedItems)
                {
                    OmReader reader = (OmReader)item.Tag;
                    FileListViewRemove(reader.Filename, true);
                }
            }
        }

        List<OmDevice> identifyDevices;
        private void devicesToolStripButtonIdentify_Click(object sender, EventArgs e)
        {
            identifyDevices = new List<OmDevice>();

            foreach (ListViewItem item in devicesListView.SelectedItems)
            {
                OmDevice device = (OmDevice) item.Tag;

                device.SetLed(OmApi.OM_LED_STATE.OM_LED_BLUE);

                identifyDevices.Add(device);
            }

            if (devicesListView.SelectedItems.Count > 0)
            {
                //Start timer to turn off led.
                identifyTimer.Enabled = true;
            }
        }

        bool turnOn = true;
        int ticks = 0;
        private void identifyTimer_Tick(object sender, EventArgs e)
        {
            if (ticks <= 10)
            {
                //Turn off lights
                foreach (OmDevice device in identifyDevices)
                {
                    if (turnOn)
                    {
                        device.SetLed(OmApi.OM_LED_STATE.OM_LED_BLUE);
                        turnOn = false;
                    }
                    else
                    {
                        device.SetLed(OmApi.OM_LED_STATE.OM_LED_OFF);
                        turnOn = true;
                    }
                }

                ticks++;
            }
            else
            {
                foreach (OmDevice device in identifyDevices)
                {
                    device.SetLed(OmApi.OM_LED_STATE.OM_LED_AUTO);
                }
                ticks = 0;
                turnOn = true;
                identifyTimer.Enabled = false;
            }
        }

        //TS - Now the plugins button
        private void devicesToolStripButtonExport_Click(object sender, EventArgs e)
        {
            RunPluginsProcess(devicesListView.SelectedItems);
        }

        #region Plugin Stuff

        private FileInfo[] getPluginInfo(string pluginName)
        {
            FileInfo[] pluginInfo = new FileInfo[3];

            string folder = Properties.Settings.Default.CurrentPluginFolder;

            DirectoryInfo d = new DirectoryInfo(folder);

            FileInfo[] allFiles = d.GetFiles("*.*");

            foreach (FileInfo f in allFiles)
            {
                Console.WriteLine("name: " + f.Name);
                if (Path.GetFileNameWithoutExtension(f.Name).Equals(pluginName))
                {
                    //We've found our plugin.
                    if (f.Extension.Equals(".html"))
                    {
                        pluginInfo[2] = f;
                    }
                    else if (f.Extension.Equals(".xml"))
                    {
                        pluginInfo[1] = f;
                    }
                    else if (f.Extension.Equals(".exe"))
                    {
                        pluginInfo[0] = f;
                    }
                }
            }

            //We should have our FileInfo array by now
            if (pluginInfo[0] != null && pluginInfo[1] != null && pluginInfo[2] != null)
            {
                return pluginInfo;
            }

            return null;
        }

        private void RunPluginsProcess(ListView.SelectedListViewItemCollection listItems)
        {
            //Find plugins and build form.
            if (listItems.Count > 0)
            {
                ListViewItem i = listItems[0];

                OmReader reader = (OmReader)i.Tag;

                List<Plugin> plugins = new List<Plugin>();

                try
                {
                    string folder = Properties.Settings.Default.CurrentPluginFolder;

                    DirectoryInfo d = new DirectoryInfo(folder);

                    List<FileInfo> htmlFiles = new List<FileInfo>();

                    FileInfo[] files = d.GetFiles("*.*");

                    //Find XML files
                    foreach (FileInfo f in files)
                    {
                        if (f.Extension == ".html")
                            htmlFiles.Add(f);
                    }

                    //Find matching other files and add to plugins dictionary
                    foreach (FileInfo f in files)
                    {
                        if (f.Extension != ".html" && f.Extension != ".xml")
                            foreach (FileInfo htmlFile in htmlFiles)
                            {
                                string name = Path.GetFileNameWithoutExtension(f.Name);
                                string xmlName = Path.GetDirectoryName(f.FullName) + "\\" + name + ".xml";

                                FileInfo xmlFile = new FileInfo(xmlName);

                                if (Path.GetFileNameWithoutExtension(f.Name).Equals(Path.GetFileNameWithoutExtension(htmlFile.Name)))
                                    plugins.Add(new Plugin(f, xmlFile, htmlFile));
                            }
                    }
                }
                catch (PluginExtTypeException)
                {
                    MessageBox.Show("Malformed Plugin file, plugins cannot be loaded until this is resolved.", "Malformed Plugin", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                //If there is more than 1 plugin then show the form otherwise give error.
                if (plugins.Count > 0)
                {
                    float blockStart = -1;
                    float blockCount = -1;

                    PluginsForm pluginsForm;

                    //Now see if we've got a selection on the dataViewer
                    if(dataViewer.HasSelection)
                    {
                        blockStart = dataViewer.SelectionBeginBlock + dataViewer.OffsetBlocks;
                        blockCount = dataViewer.SelectionEndBlock - dataViewer.SelectionBeginBlock;

                        pluginsForm = new PluginsForm(plugins, reader.Filename, blockStart, blockCount);
                        pluginsForm.ShowDialog(this);

                        //Run the process
                        if (pluginsForm.DialogResult == System.Windows.Forms.DialogResult.OK)
                        {
                            //if the plugin has an output file    
                            RunProcess(pluginsForm.SelectedPlugin, pluginsForm.rpf.ParameterString, pluginsForm.rpf.OriginalOutputName, reader.Filename);
                        }
                    }
                    else
                    {
                        //Now that we have our plugins, open the plugin form
                        pluginsForm = new PluginsForm(plugins, reader.Filename, - 1, -1);
                        pluginsForm.ShowDialog(this);

                        //Run the process
                        if (pluginsForm.DialogResult == System.Windows.Forms.DialogResult.OK)
                        {
                            //if the plugin has an output file    
                            RunProcess(pluginsForm.SelectedPlugin, pluginsForm.rpf.ParameterString, pluginsForm.rpf.OriginalOutputName, reader.Filename);
                        }
                    }
                }
                //No files so do a dialog.
                else
                {
                    MessageBox.Show("No plugins found, put plugins in the current plugins folder or change your plugin folder in the Options", "No Plugins", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                MessageBox.Show("Please choose a file to run a plugin on.");
            }
        }


        List<ListViewItem> queueListViewItems = new List<ListViewItem>();
        //PLUGIN PROCESS RUNNING
        private void RunProcess(Plugin plugin, string parametersAsString, string originalOutputName, string inputName)
        {
            //TOM TODO - Add in so we can run PY and JAR files as well as EXE
            //if (Plugin.Ext == Plugin.ExtType.PY)
            //{
            //    p.StartInfo.FileName = "python " + Plugin.RunFile.FullName;
            //}

            PluginQueueItem pqi = new PluginQueueItem(plugin, parametersAsString, inputName);

            if (originalOutputName != "")
            {
                pqi.OriginalOutputName = originalOutputName;
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = plugin.RunFile.FullName;
            //psi.FileName = @"H:\OM\Plugins\TestParameterApp.exe";

            psi.Arguments = parametersAsString;

            Console.WriteLine("arg: " + psi.Arguments);

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            psi.UseShellExecute = false;
            psi.Verb = "runas";
            psi.CreateNoWindow = true;

            pqi.StartInfo = psi;

            //Add PQI to file queue
            ListViewItem lvi = new ListViewItem(new string[] { pqi.Name, pqi.File, "0" });

            BackgroundWorker pluginQueueWorker = new BackgroundWorker();
            pluginQueueWorker.WorkerSupportsCancellation = true;

            pluginQueueWorker.DoWork += new DoWorkEventHandler(pluginQueueWorker_DoWork);
            pluginQueueWorker.ProgressChanged += new ProgressChangedEventHandler(pluginQueueWorker_ProgressChanged);
            pluginQueueWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(pluginQueueWorker_RunWorkerCompleted);

            lvi.Tag = pluginQueueWorker;

            queueListViewItems2.Items.Add(lvi);

            pluginQueueWorker.RunWorkerAsync(pqi);

            //READ STD OUT OF PLUGIN
            //We want to read the info and decide what to do based on it.
            //p - If it starts with p then percentage. [p 10%]
            //s - If it starts with s then status update. [status New Status Here]
            /*while (!p.HasExited)
            {
                string outputLine = p.StandardOutput.ReadLine();

                parseMessage(outputLine);

                //runPluginProgressBar.Invalidate(true);
                //labelStatus.Invalidate(true);
            }*/
        }

        void pluginQueueWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Result != null)
            {
            }
            else if(e.Cancelled)
            {
                //TS - TODO - Do we need a cancel message to pop up?
            }
            else if(e.Error != null)
            {
                MessageBox.Show("An error has occured in the plugin:\n" + e.Error.Message, "Error in Plugin", MessageBoxButtons.OK);
            }
        }

        void pluginQueueWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            Console.WriteLine("Progress Percentage: " + e.ProgressPercentage);
        }

        void pluginQueueWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            PluginQueueItem pqi = (PluginQueueItem) e.Argument;

            try
            {
                Process p = Process.Start(pqi.StartInfo);

                p.Start();

                //TS - TODO - This is where we need to get the info and put it into progress bar...
                //Hack - Sometimes we don't get the last stdout line
                //string lastLine = p.StandardOutput.ReadLine();
                //parseMessage(lastLine);

                p.WaitForExit();

                p.Close();

                //If there is an output file:
                if (pqi.OriginalOutputName != "")
                {
                    //HACK - Copy the output file back into the working directory.
                    string outputFileLocation = System.IO.Path.Combine(Properties.Settings.Default.CurrentWorkingFolder, pqi.OriginalOutputName);
                    System.IO.File.Copy(pqi.destFolder + "temp.csv", outputFileLocation);

                    //Delete the test csv and temp cwa
                    //System.IO.File.Delete(pqi.destFolder + "temp.csv");
                    //System.IO.File.Delete(tempCWAFilePath);
                }
            }
            catch (InvalidOperationException ioe)
            {
                Console.WriteLine("IOE: " + ioe.Message);
            }
            catch (System.ComponentModel.Win32Exception w32e)
            {
                Console.WriteLine("w32e: " + w32e.Message);
            }
            catch (Exception e2)
            {
                Console.WriteLine("e: " + e2.Message);
            }
        }

        private void parseMessage(string outputLine)
        {
            //OUTPUT
            if (outputLine != null)
            {
                if (outputLine[0] == 'p')
                {
                    string percentage = outputLine.Split(' ').ElementAt(1);
                    //runPluginProgressBar.Value = Int32.Parse(percentage);
                }
                else if (outputLine[0] == 's')
                {
                    string message = outputLine.Split(new char[] { ' ' }, 2).Last();
                    //labelStatus.Text = message;
                }
            }
            Console.WriteLine("o: " + outputLine);
        }

        #endregion

        //TS - This will become a plugin so keep the code here for now just so we can see the Form.
        //ExportDataConstruct(OmSource.SourceCategory.File);

        private void gotoDefaultFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.CurrentWorkingFolder = Properties.Settings.Default.DefaultWorkingFolder;

            LoadWorkingFolder(true);
        }

        private void setDefaultWorkingFolderToCurrentWorkingFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.DefaultWorkingFolder = Properties.Settings.Default.CurrentWorkingFolder;
        }

        private void pluginsToolStripButton_Click(object sender, EventArgs e)
        {
            RunPluginsProcess(filesListView.SelectedItems);
        }

        private void openCurrentWorkingFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.ShowDialog();
        }

        private void toolStripMain_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void DeleteFilesToolStripButton_Click_1(object sender, EventArgs e)
        {
            if (filesListView.SelectedItems.Count > 0)
            {
                DialogResult d = MessageBox.Show("Are you sure you want to delete?", "Delete", MessageBoxButtons.OKCancel);

                if (d == System.Windows.Forms.DialogResult.OK)
                {
                    for (int i = 0; i < filesListView.SelectedItems.Count; i++)
                    {
                        OmReader r = (OmReader)filesListView.SelectedItems[i].Tag;

                        FileListViewRemove(r.Filename, true);
                    }
                }
            }
        }

        #region Dynamic ToolStrip Files Buttons

        //TS - If we have clicked over to the queue tab then change the toolstrip items
        private void rebuildQueueToolStripItems()
        {
            toolStripFiles.Items.Clear();
            ToolStripButton tsb = new ToolStripButton("Cancel", Properties.Resources.DeleteHS);
            tsb.Click += new EventHandler(tsb_Click2);
            tsb.Enabled = false;

            toolStripFiles.Items.Add(tsb);
        }

        
        //Tool Strip Cancel button
        void tsb_Click2(object sender, EventArgs e)
        {
            //Delete
            if (queueListViewItems2.SelectedItems.Count > 0)
            {
                foreach(ListViewItem lvi in queueListViewItems2.Items)
                {
                    queueListViewItems2.Items.Remove(lvi);

                    BackgroundWorker bw = (BackgroundWorker)lvi.Tag;
                    //TS - TODO - Need to actually know when it is killed or if it is...
                    bw.CancelAsync();
                }
            }
        }

        private void rebuildFilesToolStripItems()
        {
            toolStripFiles.Items.Clear();
            ToolStripButton tsbPlugins = new ToolStripButton("Plugins", Properties.Resources.Export);
            tsbPlugins.Click += new EventHandler(devicesToolStripButtonExport_Click);
            tsbPlugins.Enabled = false;

            ToolStripButton tsbDelete = new ToolStripButton("Delete", Properties.Resources.DeleteHS);
            tsbDelete.Click += new EventHandler(DeleteFilesToolStripButton_Click);
            tsbDelete.Enabled = false;

            toolStripFiles.Items.Add(tsbPlugins);
            toolStripFiles.Items.Add(tsbDelete);

            AddProfilePluginsToToolStrip();
        }

        private void tabControlFiles_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabControlFiles.SelectedIndex == 0)
            {
                rebuildFilesToolStripItems();
            }
            else
            {
                rebuildQueueToolStripItems();
            }
        }

        #endregion

        private void queueListViewItems2_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (queueListViewItems2.SelectedItems.Count > 0)
            {
                for (int i = 0; i < toolStripFiles.Items.Count; i++)
                {
                    toolStripFiles.Items[i].Enabled = true;
                }
            }
            else
            {
                for (int i = 0; i < toolStripFiles.Items.Count; i++)
                {
                    toolStripFiles.Items[i].Enabled = false;
                }
            }
        }
    }
}
