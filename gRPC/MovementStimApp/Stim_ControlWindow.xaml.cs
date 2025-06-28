using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls.Primitives;
using System.Xml;
using System.Collections.ObjectModel;
using System.IO.Packaging;

namespace MovementStimAPP
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class Stim_ControlWindow : Window
    {
        private BICManager aBICManager = new BICManager();
        private EMGLib.Delsys_Connection baseConnection = new EMGLib.Delsys_Connection();
        private EMGLib.EMG_Streaming emgStreaming = new EMGLib.EMG_Streaming();
        public CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        Thread emgStreamThread;
        Thread filtEMGThread;
        Thread plotRawThread;
        Thread plotFiltThread;

        Thread startStimThread;

        List<float>[] emgRawData;
        List<float>[] emgFiltData;
        List<double>[] threshData;
        List<int>[] stimData;
        int numChannels = 16;
        int bicChannels = 34;

        emgConfiguration EMGconfigInfo;

        private System.Timers.Timer EMGChartUpdateTimer;
        private System.Timers.Timer neuroStreamChartUpdateTimer;

        private bool calibrating = false;
        private bool delsysConnected = false;
        private bool bicConnected = false;
        private string dateStamp;

        public class Channel
        {
            public string Name { get; set; }
            public bool IsSelected { get; set; }
        }
        public class Participant
        {
            public string ID { get; set; }
            public bool IsSelected { get; set; }
        }
        public class emgConfiguration
        {
            public List<string> partIDs { get; set; }
            public string save_path { get; set; }
        }


        public List<Channel> channelList { get; set; }
        public List<Channel> bicList { get; set; }
        public ObservableCollection<Participant> participantList { get; set; }
        public List<int> channelNumList { get; set; }

        //public float[] maxSig;
        //public float percentThresh;
        public Stim_ControlWindow()
        {

            InitializeComponent();
            channelList = new List<Channel>();
            channelNumList = new List<int>();

            bicList = new List<Channel>();

            for (int i = 1; i <= numChannels; i++)
            {
                channelList.Add(new Channel { IsSelected = false, Name = i.ToString() });
                channelNumList.Add(i);
            }

            for (int i = 1; i <=bicChannels; i++)
            {
                bicList.Add(new Channel { IsSelected = false, Name=i.ToString() });
            }
            this.DataContext = this;

        }

        private void ControlWindow_Closed(object sender, EventArgs e)
        {
            if (bicConnected)
            {
                aBICManager.Dispose();
                neuroStreamChartUpdateTimer.Stop();
            }
            if(delsysConnected)
            {
                EMGChartUpdateTimer.Stop();
                cancellationTokenSource.Cancel();
                emgStreaming.emgDataPort_Diconnect();

                plotRawThread.Abort();
                plotFiltThread.Abort();
                filtEMGThread.Abort();
                emgStreamThread.Abort();

                baseConnection.SendCommand("STOP");
                baseConnection.SendCommand("QUIT");
            }

        }
        private void ControlWindow_Loaded(object sender, RoutedEventArgs e)
        {
            participantList = new ObservableCollection<Participant>();
            dateStamp = $"{DateTime.Now:yyyy - MM - dd}";
            var colors_list = new System.Drawing.Color[]
            {
                System.Drawing.Color.Blue,
                System.Drawing.Color.Red,
                System.Drawing.Color.Green,
                System.Drawing.Color.DarkOrchid,
                System.Drawing.Color.DeepPink,
                System.Drawing.Color.Orange,
                System.Drawing.Color.Plum,
                System.Drawing.Color.LightSteelBlue,
                System.Drawing.Color.Maroon,
                System.Drawing.Color.OrangeRed,
                System.Drawing.Color.Gold,
                System.Drawing.Color.LightCoral,
                System.Drawing.Color.Gray,
                System.Drawing.Color.Navy,
                System.Drawing.Color.OliveDrab,
                System.Drawing.Color.Salmon,
                System.Drawing.Color.SandyBrown,
                System.Drawing.Color.Magenta,
                System.Drawing.Color.Purple,
                System.Drawing.Color.RoyalBlue,
                System.Drawing.Color.Black,
                System.Drawing.Color.CadetBlue,
                System.Drawing.Color.Crimson,
                System.Drawing.Color.DarkCyan,
                System.Drawing.Color.DarkMagenta,
                System.Drawing.Color.Tan,
                System.Drawing.Color.Tomato,
                System.Drawing.Color.ForestGreen,
                System.Drawing.Color.RosyBrown,
                System.Drawing.Color.MediumPurple,
                System.Drawing.Color.Sienna,
                System.Drawing.Color.Indigo,
                System.Drawing.Color.Aqua
};
            //EMG
            btn_emgConfigLoad.IsEnabled = true;
            btn_connectEMG.IsEnabled = false;
            btn_startEMGlog.IsEnabled = false;
            btn_stopEMGlog.IsEnabled = false;
            btn_disconnectEMG.IsEnabled = false;
            //BIC
            btn_bicConfigLoad.IsEnabled = true;
            btn_connectBIC.IsEnabled = false;
            btn_disconnectBIC.IsEnabled = false;
            //Stim
            calibration_checkbox.IsEnabled = true;
            btn_loadCalib.IsEnabled = true;
            partSelect.IsEnabled = false;
            btn_threshSave.IsEnabled = false;
            btn_startStim.IsEnabled = false;
            btn_stopStim.IsEnabled = false;

            string seriesName;

            EMGStreamChart.ChartAreas[0].AxisX.Title = "Time (ns)";
            EMGStreamChart.ChartAreas[0].AxisY.Title = "Raw EMG signal (V)";

            EMGStreamChart.ChartAreas[0].AxisY.Minimum = -0.1e-3;
            EMGStreamChart.ChartAreas[0].AxisY.Maximum = 0.1e-3;
            EMGStreamChart.Series.Clear();

            FiltEMGStreamChart.ChartAreas[0].AxisX.Title = "Time (ns)";
            FiltEMGStreamChart.ChartAreas[0].AxisY.Title = "Filt EMG signal (V)";

            FiltEMGStreamChart.ChartAreas[0].AxisY.Minimum = -0.1e-3;
            FiltEMGStreamChart.ChartAreas[0].AxisY.Maximum = 0.1e-3;
            FiltEMGStreamChart.Series.Clear();

            for (int i = 1; i <= numChannels; i++)
            {
                seriesName = "EMG " + i.ToString();
                EMGStreamChart.Series.Add(
                new System.Windows.Forms.DataVisualization.Charting.Series
                {
                    Name = seriesName,
                    Color = colors_list[i - 1],
                    ChartType = System.Windows.Forms.DataVisualization.Charting.SeriesChartType.FastLine
                });

                FiltEMGStreamChart.Series.Add(
                new System.Windows.Forms.DataVisualization.Charting.Series
                {
                    Name = seriesName,
                    Color = colors_list[i - 1],
                    ChartType = System.Windows.Forms.DataVisualization.Charting.SeriesChartType.FastLine
                });


                //// when loading window, make legend invisible
                EMGStreamChart.Series[i - 1].IsVisibleInLegend = false;
                FiltEMGStreamChart.Series[i - 1].IsVisibleInLegend = false;
            }
            if (!calibrating)
            {
                // add threshold and stim to chart series
                for (int i = 1; i <= numChannels; i++)
                {
                    seriesName = "Thresh " + i.ToString();
                    EMGStreamChart.Series.Add(
                    new System.Windows.Forms.DataVisualization.Charting.Series
                    {
                        Name = seriesName,
                        Color = colors_list[i - 1],
                        ChartType = System.Windows.Forms.DataVisualization.Charting.SeriesChartType.FastLine
                    });

                    FiltEMGStreamChart.Series.Add(
                    new System.Windows.Forms.DataVisualization.Charting.Series
                    {
                        Name = seriesName,
                        Color = colors_list[i - 1],
                        ChartType = System.Windows.Forms.DataVisualization.Charting.SeriesChartType.FastLine
                    });


                    // when loading window, make legend invisible
                    EMGStreamChart.Series[numChannels + i - 1].IsVisibleInLegend = false;
                    FiltEMGStreamChart.Series[numChannels + i - 1].IsVisibleInLegend = false;
                }
                for (int i = 1; i <= numChannels; i++)
                {
                    seriesName = "Stim " + i.ToString();
                    EMGStreamChart.Series.Add(
                    new System.Windows.Forms.DataVisualization.Charting.Series
                    {
                        Name = seriesName,
                        Color = colors_list[i - 1],
                        ChartType = System.Windows.Forms.DataVisualization.Charting.SeriesChartType.FastLine
                    });

                    FiltEMGStreamChart.Series.Add(
                    new System.Windows.Forms.DataVisualization.Charting.Series
                    {
                        Name = seriesName,
                        Color = colors_list[i - 1],
                        ChartType = System.Windows.Forms.DataVisualization.Charting.SeriesChartType.FastLine
                    });


                    // when loading window, make legend invisible
                    EMGStreamChart.Series[numChannels + i - 1].IsVisibleInLegend = false;
                    FiltEMGStreamChart.Series[numChannels + i - 1].IsVisibleInLegend = false;

                }

            }
            for (int i = 0; i < FiltEMGStreamChart.Series.Count; i++)
            {
                EMGStreamChart.Series[i].IsVisibleInLegend = false;
                FiltEMGStreamChart.Series[i].IsVisibleInLegend = false;
            }

            if (!calibrating)
            {
                neuroStreamChart.ChartAreas[0].AxisX.Title = "Time (ns)";
                neuroStreamChart.ChartAreas[0].AxisY.Title = "BIC signal (V)";

                //neuroStreamChart.ChartAreas[0].AxisY.Minimum = -0.1e-3;
                //neuroStreamChart.ChartAreas[0].AxisY.Maximum = 0.1e-3;
                neuroStreamChart.Series.Clear();
                for (int i = 1; i < bicChannels; i++)
                {
                    if (i == 33)
                    {
                        seriesName = "Filtered Channel";
                    }
                    else
                    {
                        seriesName = "Channel " + i.ToString();
                    }
                    neuroStreamChart.Series.Add(
                    new System.Windows.Forms.DataVisualization.Charting.Series
                    {
                        Name = seriesName,
                        Color = colors_list[i - 1],
                        ChartType = System.Windows.Forms.DataVisualization.Charting.SeriesChartType.FastLine
                    });

                    // when loading window, make legend invisible
                    neuroStreamChart.Series[i - 1].IsVisibleInLegend = false;
                }
            }

        }


        private void btn_emgConfigLoad_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // open dialog box to select file with patient-specific settings
                var fileD = new Microsoft.Win32.OpenFileDialog();
                bool? loadFile = fileD.ShowDialog();
                if (loadFile == true)
                {
                    string fileName = fileD.FileName;
                    if (File.Exists(fileName))
                    {
                        // load in .json file and read in stimulation parameters
                        using (StreamReader fileReader = new StreamReader(fileName))
                        {
                            string configJson = fileReader.ReadToEnd();
                            EMGconfigInfo = System.Text.Json.JsonSerializer.Deserialize<emgConfiguration>(configJson);
                        }
                    }
                }
                participantList.Clear();
                for (int par = 0; par < EMGconfigInfo.partIDs.Count; par++)
                {
                    participantList.Add(new Participant { IsSelected = false, ID = EMGconfigInfo.partIDs[par] });
                }
                partSelect.IsEnabled = true;

            }
            catch (Exception ex)
            {
                Console.WriteLine("Load Config threw and exception: " + ex.ToString());
            }

            
        }
        private void btn_bicConfigLoad_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // open dialog box to select file with patient-specific settings
                var fileD = new Microsoft.Win32.OpenFileDialog();
                bool? loadFile = fileD.ShowDialog();
                if (loadFile == true)
                {
                    string fileName = fileD.FileName;
                    if (File.Exists(fileName))
                    {
                        // load in .json file and read in stimulation parameters
                        using (StreamReader fileReader = new StreamReader(fileName))
                        {
                            string configJson = fileReader.ReadToEnd();
                            aBICManager.configInfo = System.Text.Json.JsonSerializer.Deserialize<BICManager.Configuration>(configJson);

                        }

                    }
                }
            }
            catch (Exception theException)
            {
                Console.WriteLine(theException.Message);
            }
        }
        private void part_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedPart = e.AddedItems[0];
            var part = selectedPart as Participant;
            if (part != null)
            {
                btn_connectEMG.IsEnabled = true;
                emgStreaming.currPart = part.ID;
            }
        }


        private void calibration_Checked(object sender, RoutedEventArgs e)
        {
            calibrating = true;
            btn_loadCalib.IsEnabled = false;
            btn_bicConfigLoad.IsEnabled = false;

        }
        private void calibration_Unchecked(object sender, RoutedEventArgs e)
        {
            calibrating = false;
            btn_loadCalib.IsEnabled = true;
            btn_bicConfigLoad.IsEnabled = true;
        }
        private void btn_loadCalib_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                //maxSig = new float[numChannels];
                int ch = 0;
                // open dialog box to select file with patient-specific settings
                var fileD = new Microsoft.Win32.OpenFileDialog();
                bool? loadFile = fileD.ShowDialog();
                if(loadFile == true)
                {
                    string fileName = fileD.FileName;
                    if (File.Exists(fileName))
                    {
                        // load in the .csv file and read the calibration data i.e. the maximal contraction [filtered] value 
                        using(StreamReader fileReader = new StreamReader(fileName))
                        {
                            while (!fileReader.EndOfStream)
                            {
                                var line = fileReader.ReadLine();
                                var values = line.Split(',');
                                float maxVal;
                                float.TryParse(values[1], out maxVal);
                                //maxSig[ch] = maxVal;
                                emgStreaming._stimMod.maxSig[ch] = maxVal;
                                ch++;
                            }
                            emgStreaming._stimMod.setThresh();
                        }
                        Console.WriteLine("Calibration Data was loaded");

                    }
                    else
                    {
                        Console.WriteLine("Calibration Data was not loaded");
                    }
                    
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }


        private void btn_connectEMG_Click(object sender, RoutedEventArgs e)
        {
            cancellationTokenSource = new CancellationTokenSource();
            baseConnection.Main();
            // create/recreate threads
            emgStreaming.emgDataPort_Connect();
            emgStreaming.currPart = "testMTS";
            emgStreamThread = new Thread(() => emgStreaming.StreamEMG(cancellationTokenSource.Token, "C:\\Users\\MRPICS\\Desktop\\Maryam\\Data", dateStamp));
            filtEMGThread = new Thread(() => emgStreaming.filtEMGstream(cancellationTokenSource.Token));
            
            plotRawThread = new Thread(() => emgStreaming.prepRawForPlot(cancellationTokenSource.Token));
            plotFiltThread = new Thread(() => emgStreaming.prepFiltForPlot(cancellationTokenSource.Token));

            // Start the threads
            emgStreamThread.Start();
            filtEMGThread.Start();
            plotRawThread.Start();
            plotFiltThread.Start();
            
            // send command to base to start streaming
            baseConnection.SendCommand("START");
            delsysConnected = true;

            // Start update timer
            EMGChartUpdateTimer = new System.Timers.Timer(200);
            EMGChartUpdateTimer.Elapsed += EMGChartUpdateTimer_Elapsed;
            EMGChartUpdateTimer.Start();

        }
        private void btn_connectBIC_Click(object sender, RoutedEventArgs e)
        {
            // connect to BIC
            aBICManager.Initialize(1000);
            aBICManager.BICConnect();
            bicConnected = true;

            neuroStreamChartUpdateTimer = new System.Timers.Timer(200);
            neuroStreamChartUpdateTimer.Elapsed += neuroChartUpdateTimer_Elapsed;
            neuroStreamChartUpdateTimer.Start();

        }
        private void btn_disconnectEMG_Click(object senser, RoutedEventArgs e)
        {

        }
        private void btn_disconnectBIC_Click(object senser, RoutedEventArgs e)
        {

        }

        // TO DO: modify to start recording when this is hit and add a start stim button
        private void btn_startEMGlog_Click(object sender, RoutedEventArgs e)
        {
            emgStreaming.logging = true;

        }

        private void btn_stopEMGlog_Click(object sender, RoutedEventArgs e)
        {
            emgStreaming.logging = false;

        }

        bool OLstimON = false;

        private void btn_thresh_Click(object sender, RoutedEventArgs e)
        {
            //thresh = new float[numChannels];
            //percentThresh = float.Parse(percentThresh_textbox.Text);
            //for (int ch = 0; ch < numChannels; ch++)
            //{
            //    thresh[ch] = maxSig[ch] * percent / 100;
            //}
            emgStreaming._stimMod.percent = float.Parse(percentThresh_textbox.Text);
        }

        private void btn_startStim_Click(object sender, RoutedEventArgs e)
        {
            startStimThread = new Thread(() => Stimulator());

            var configInfo = aBICManager.configInfo;
            try
            {

                aBICManager.enableOpenLoopStimulation(true, configInfo.monopolar, (uint)configInfo.stimChannel - 1, (uint)configInfo.returnChannel - 1, configInfo.stimAmplitude, configInfo.stimDuration, 4, configInfo.stimPeriod - (5 * configInfo.stimDuration) - 3500, configInfo.stimThreshold);
                Console.WriteLine("OL enabled");
            }
            catch
            {
                // Exception occured, gRPC command did not succeed, do not update UI button elements
                Console.WriteLine("Open loop stimulation NOT started: load new configuration\n");

                return;
            }

            emgStreaming._stimEnabled = true;
            startStimThread.Start();
        }

        private void btn_stopStim_Click(Object sender, RoutedEventArgs e)
        {
            BICManager.Configuration configInfo = aBICManager.configInfo;
            emgStreaming._stimEnabled = false;
            aBICManager.enableOpenLoopStimulation(false, configInfo.monopolar, (uint)configInfo.stimChannel - 1, (uint)configInfo.returnChannel - 1, configInfo.stimAmplitude, configInfo.stimDuration, 1, 20000, configInfo.stimThreshold);

            startStimThread.Abort();
            //btn_stopStim.IsEnabled = false;
            //btn_startStim.IsEnabled = true;
            //btn_threshSave.IsEnabled = true;
        }


        private void emgCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // look for the selected items in the listbox
            List<int> selectedChannels = new List<int>();
            string chanString = "";
            int chanVal;
            bool valConvert = false;

            // get a list of selected channels
            var selected = from item in channelList
                           where item.IsSelected == true
                           select item.Name.ToString();

            // convert from string to int type
            foreach (String item in selected)
            {
                valConvert = Int32.TryParse(item, out chanVal);
                selectedChannels.Add(chanVal);
            }

            // reset current legend
            EMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
            delegate
            {
                foreach (var series in EMGStreamChart.Series)
                {
                    series.IsVisibleInLegend = false;
                    series.Enabled = false;
                }
            }));

            FiltEMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
            delegate
            {
                foreach (var series in FiltEMGStreamChart.Series)
                {
                    series.IsVisibleInLegend = false;
                    series.Enabled = false;
                }
            }));


            // update legend for data streaming chart for newest selection of channels
            for (int i = 0; i < selectedChannels.Count; i++)
            {
                chanString = "EMG " + selectedChannels[i].ToString();

                EMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                delegate
                {
                    EMGStreamChart.Series[chanString].IsVisibleInLegend = true;
                    EMGStreamChart.Series[chanString].Enabled = true;
                }));
                FiltEMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                delegate
                {
                    FiltEMGStreamChart.Series[chanString].IsVisibleInLegend = true;
                    FiltEMGStreamChart.Series[chanString].Enabled = true;
                }));

                //if (!calibrating)
                //{
                //    // add threshold to legend for each selected channel
                //    chanString = "Thresh " + selectedChannels[i].ToString();
                //    EMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                //    delegate
                //    {
                //        EMGStreamChart.Series[chanString].IsVisibleInLegend = true;
                //        EMGStreamChart.Series[chanString].Enabled = true;
                //    }));
                //    FiltEMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                //    delegate
                //    {
                //        FiltEMGStreamChart.Series[chanString].IsVisibleInLegend = true;
                //        FiltEMGStreamChart.Series[chanString].Enabled = true;
                //    }));

                //    // add stim occurrance to legend for each selected channel
                //    chanString = "Stim " + selectedChannels[i].ToString();
                //    EMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                //    delegate
                //    {
                //        EMGStreamChart.Series[chanString].IsVisibleInLegend = true;
                //        EMGStreamChart.Series[chanString].Enabled = true;
                //    }));
                //    FiltEMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                //    delegate
                //    {
                //        FiltEMGStreamChart.Series[chanString].IsVisibleInLegend = true;
                //        FiltEMGStreamChart.Series[chanString].Enabled = true;
                //    }));
                //}

                //// if not calibrating, it means stim app is on and an extrac stream channels visualizes stim
                if (i + 1 == selectedChannels.Count)
                {
                    //if (!calibrating)
                    //{
                    //    chanString = "Stim";

                    //    EMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                    //    delegate
                    //    {
                    //        EMGStreamChart.Series[chanString].IsVisibleInLegend = true;
                    //        EMGStreamChart.Series[chanString].Enabled = true;
                    //    }));
                    //    FiltEMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                    //    delegate
                    //    {
                    //        FiltEMGStreamChart.Series[chanString].IsVisibleInLegend = true;
                    //        FiltEMGStreamChart.Series[chanString].Enabled = true;
                    //    }));
                    //}
                }
            }


        }

        private void bicCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // look for the selected items in the listbox
            List<int> selectedChannels = new List<int>();
            string chanString = "";
            int chanVal;
            bool valConvert = false;

            // get a list of selected channels
            var selected = from item in bicList
                           where item.IsSelected == true
                           select item.Name.ToString();

            // convert from string to int type
            foreach (String item in selected)
            {
                valConvert = Int32.TryParse(item, out chanVal);
                if (valConvert)
                {
                    selectedChannels.Add(chanVal);
                }
                else
                {
                    selectedChannels.Add(33);
                }
            }

            // reset current legend
            neuroStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
            delegate
            {
                foreach (var series in neuroStreamChart.Series)
                {
                    series.IsVisibleInLegend = false;
                    series.Enabled = false;
                }
            }));

            // update legend for newest selection of channels
            for (int i = 0; i < selectedChannels.Count; i++)
            {
                if (selectedChannels[i] == 33)
                {
                    chanString = "Filtered Channel";
                }
                else
                {
                    chanString = "Channel " + selectedChannels[i].ToString();
                }
                neuroStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                delegate
                {
                    neuroStreamChart.Series[chanString].IsVisibleInLegend = true;
                    neuroStreamChart.Series[chanString].Enabled = true;
                }));
            }
        }

        private void EMGChartUpdateTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            // grab latest data and corresponding timestamp
            emgRawData = emgStreaming.getRawData();
            emgFiltData = emgStreaming.getFiltData();
            //if (!calibrating)
            //{
            //    // get data
            //    (threshData, stimData) = emgStreaming.getMovementStimData();
            //}

            // look for the selected items in the listbox
            List<int> selectedChannels = new List<int>();
            string chanString = "";
            int chanVal;
            bool valConvert = false;

            // get a list of selected channels
            var selected = from item in channelList
                           where item.IsSelected == true
                           select item.Name.ToString();

            // convert from string to int type
            foreach (String item in selected)
            {
                valConvert = Int32.TryParse(item, out chanVal);
                selectedChannels.Add(chanVal);
            }
            for (int i = 0; i < selectedChannels.Count; i++)
            {
                chanString = "EMG " + selectedChannels[i].ToString();
                var emgRaw_output = emgRawData.Select(row => row[selectedChannels[i] - 1]).ToList();

                try
                {

                    EMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                    delegate
                    {
                        EMGStreamChart.Series[chanString].Points.DataBindY(emgRaw_output);
                    }));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Chart threw an exception: " + ex.Message);
                }
                var emgFilt_output = emgFiltData.Select(row => row[selectedChannels[i] - 1]).ToList();
                try
                {

                    FiltEMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                    delegate
                    {
                        FiltEMGStreamChart.Series[chanString].Points.DataBindY(emgFilt_output);
                    }));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Chart threw an exception: " + ex.Message);
                }


                //if (!calibrating)
                //{
                //    // update chart for thresh
                //    chanString = "Thresh " + selectedChannels[i].ToString();
                //    var thresh_output = threshData.Select(row => row[selectedChannels[i] - 1]).ToList();

                //    try
                //    {

                //        EMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                //        delegate
                //        {
                //            EMGStreamChart.Series[chanString].Points.DataBindY(thresh_output);
                //        }));
                //    }
                //    catch (Exception ex)
                //    {
                //        Console.WriteLine("Chart threw an exception: " + ex.Message);
                //    }
                //    try
                //    {

                //        FiltEMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                //        delegate
                //        {
                //            FiltEMGStreamChart.Series[chanString].Points.DataBindY(thresh_output);
                //        }));
                //    }
                //    catch (Exception ex)
                //    {
                //        Console.WriteLine("Chart threw an exception: " + ex.Message);
                //    }

                //    // update chart for stim
                //    chanString = "Stim " + selectedChannels[i].ToString();
                //    var stim_output = stimData.Select(row => row[selectedChannels[i] - 1]).ToList();

                //    try
                //    {

                //        EMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                //        delegate
                //        {
                //            EMGStreamChart.Series[chanString].Points.DataBindY(stim_output);
                //        }));
                //    }
                //    catch (Exception ex)
                //    {
                //        Console.WriteLine("Chart threw an exception: " + ex.Message);
                //    }
                //    emgFilt_output = emgFiltData.Select(row => row[selectedChannels[i] - 1]).ToList();
                //    try
                //    {

                //        FiltEMGStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                //        delegate
                //        {
                //            FiltEMGStreamChart.Series[chanString].Points.DataBindY(stim_output);
                //        }));
                //    }
                //    catch (Exception ex)
                //    {
                //        Console.WriteLine("Chart threw an exception: " + ex.Message);
                //    }
                //}

            }

        }

        private void neuroChartUpdateTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            // grab latest data
            List<double>[] neuroData = aBICManager.getData();

            // look for the selected items in the listbox
            List<int> selectedChannels = new List<int>();
            string chanString = "";
            int chanVal;
            bool valConvert = false;

            // get a list of selected channels
            var selected = from item in bicList
                           where item.IsSelected == true
                           select item.Name.ToString();

            // convert from string to int type
            foreach (String item in selected)
            {
                valConvert = Int32.TryParse(item, out chanVal);
                if (valConvert)
                {
                    selectedChannels.Add(chanVal);
                }
                else
                {
                    selectedChannels.Add(33);
                }
            }

            // update plot with newest data for selected channels
            for (int i = 0; i < selectedChannels.Count; i++)
            {
                if (selectedChannels[i] == 33)
                {
                    chanString = "Filtered Channel";
                }
                else
                {
                    chanString = "Channel " + selectedChannels[i].ToString();
                }
                neuroStreamChart.Invoke(new System.Windows.Forms.MethodInvoker(
                delegate
                {
                    neuroStreamChart.Series[chanString].Points.DataBindY(neuroData[selectedChannels[i] - 1]);
                }));
            }
        }






        private void Stimulator()
        {
            var configInfo = aBICManager.configInfo;
            while (emgStreaming._stimEnabled)
            {
                if (emgStreaming._generateStim)
                {
                    if (!OLstimON)
                    {
                        try
                        {

                            aBICManager.enableOpenLoopStimulation(true, configInfo.monopolar, (uint)configInfo.stimChannel - 1, (uint)configInfo.returnChannel - 1, configInfo.stimAmplitude, configInfo.stimDuration, 4, configInfo.stimPeriod - (5 * configInfo.stimDuration) - 3500, configInfo.stimThreshold);
                            OLstimON = true;
                            Console.WriteLine("OL enabled, " + OLstimON);

                        }
                        catch
                        {
                            // Exception occured, gRPC command did not succeed, do not update UI button elements
                            Console.WriteLine("Open loop stimulation NOT started: load new configuration\n");

                            return;
                        }
                        Thread.Sleep(20);
                    }

                }
                else
                {
                    if (OLstimON)
                    {
                        try
                        {
                            aBICManager.enableOpenLoopStimulation(false, configInfo.monopolar, (uint)configInfo.stimChannel - 1, (uint)configInfo.returnChannel - 1, configInfo.stimAmplitude, configInfo.stimDuration, 1, 20000, configInfo.stimThreshold);
                            OLstimON = false;
                            Console.WriteLine("OL enabled, " + OLstimON);

                        }
                        catch
                        {
                            // Exception occured, gRPC command did not succeed, do not update UI button elements
                            Console.WriteLine("Open loop stimulation NOT stopped: load new configuration\n");

                            return;
                        }
                        Thread.Sleep(20);
                    }
                }

            }
            if (OLstimON)
            {
                try
                {
                    aBICManager.enableOpenLoopStimulation(false, configInfo.monopolar, (uint)configInfo.stimChannel - 1, (uint)configInfo.returnChannel - 1, configInfo.stimAmplitude, configInfo.stimDuration, 1, 20000, configInfo.stimThreshold);
                    OLstimON = false;
                    Console.WriteLine("OL enabled, " + OLstimON);

                }
                catch
                {
                    // Exception occured, gRPC command did not succeed, do not update UI button elements
                    Console.WriteLine("Open loop stimulation NOT stopped: load new configuration\n");

                    return;
                }
                Thread.Sleep(20);
            }
            OLstimON = false;
        }


    }
}
