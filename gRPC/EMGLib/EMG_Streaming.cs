using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Collections.Generic;

namespace EMGLib
{
    public class EMG_Streaming
    {
        // TCP/IP parameters
        TcpClient emgSocket;
        NetworkStream emgStream;
        BinaryReader emgReader;

        // EMG parameters
        private int emgDataPort = 50043;
        private int numberOfChannels = 16; // Number of Trigno Delsys EMGs corresponding to number of channels
        private int bytesPerChannel = 4; // Port 50043 processes 4-byte float per sample per channel
        private int bytesPerSample;

        // For Plotting 
        private int numSamplesToPlot = 2000;
        private List<float>[] r_emgDataToPlot; // raw data
        private List<float>[] f_emgDataToPlot; // filt data
        private Object plotDataLock = new Object();
        private Object plotFiltLock = new object();

        private List<float>[] rawData;
        private List<float>[] filtData;

        private List<double>[] threshData;
        private List<int>[] stimData;

        public bool streamingStatus = false;

        // for logging
        StreamWriter emgSW;
        StreamWriter emgFiltSW;
        StreamWriter emgEnvelopedSW;
        StreamWriter emgTimestampSW;
        public string currPart;
        public bool logging = false;
        string file_extension;
        string save_path;

        private BlockingCollection<float[]> rawSamplesQueueForPlot = new BlockingCollection<float[]>();
        private BlockingCollection<rawPacket> rawSamplesQueueForProcc = new BlockingCollection<rawPacket>();
        private BlockingCollection<float[]> filtSamplesQueueForPlot = new BlockingCollection<float[]>();

        // related to streaming
        private BlockingCollection<byte[]> bytesBufferQueue = new BlockingCollection<byte[]>();
        private BlockingCollection<float[]> rawSamplesQueue = new BlockingCollection<float[]>();
        private BlockingCollection<List<float>> rawSamplesListQueue = new BlockingCollection<List<float>>();
        private BlockingCollection<long> originalTimestampQueue = new BlockingCollection<long>();
        private BlockingCollection<int> bytesAvailableQueue = new BlockingCollection<int>();
        private BlockingCollection<List<float>[]> plotRawDataQueue = new BlockingCollection<List<float>[]>();
        private BlockingCollection<List<float>[]> plotFiltDataQueue = new BlockingCollection<List<float>[]>();

        // related to filter
        public Processing_Modules _processingMod;
        private BlockingCollection<float[]> filtSamplesQueue = new BlockingCollection<float[]>();

        // related to stim
        public Stim_Modules _stimMod;
        private BlockingCollection<double[]> threshSamplesQueue = new BlockingCollection<double[]>();
        private BlockingCollection<int[]> stimSamplesQueue = new BlockingCollection<int[]>();
        private BlockingCollection<List<double>[]> plotThreshDataQueue = new BlockingCollection<List<double>[]>();
        private BlockingCollection<List<int>[]> plotStimDataQueue = new BlockingCollection<List<int>[]>();

        public bool _generateStim = false;
        public bool _stimEnabled = false;

        private static long streamStart_timestamp;

        // calibration bool, if true stim is disabled, if false stim is enabled
        public bool calibrationOn = false;

        private struct rawPacket
        {
            public rawPacket(float[] rawSamples, long timestamp)
            {
                samples = rawSamples;
                stamp = timestamp;
            }

            public float[] samples { get; }
            public long stamp { get; }

        }

        public EMG_Streaming()
        { 
            bytesPerSample = numberOfChannels * bytesPerChannel;
            r_emgDataToPlot = new List<float>[numSamplesToPlot];
            f_emgDataToPlot = new List<float>[numSamplesToPlot];

            file_extension = $"{DateTime.Now:yyyy - MM - dd_HH - mm - ss}.csv";
            // 
            rawData = new List<float>[numSamplesToPlot];
            filtData = new List<float>[numSamplesToPlot];
            threshData = new List<double>[numSamplesToPlot];
            stimData = new List<int>[numSamplesToPlot];
            //

            _processingMod = new Processing_Modules(numberOfChannels);
            _stimMod = new Stim_Modules(numberOfChannels);



            // create a non-null array of lists 
            for (int i = 0; i < numSamplesToPlot; i++)
            {
                float[] data = new float[numberOfChannels];
                for (int ch = 0; ch < numberOfChannels; ch++)
                    data[ch] = 0f;
                r_emgDataToPlot[i] = new List<float>(data);
                f_emgDataToPlot[i] = new List<float>(data);
            }


        }

        public void emgDataPort_Connect()
        {

            emgSocket = new TcpClient();
            //emgSocket.ReceiveBufferSize = bytesPerSample; ////***
            emgSocket.Connect("localhost", emgDataPort);
            emgSocket.NoDelay = true; // sending small amounts of data is inefficient, however, pushes for immediate response

        }
        public void emgDataPort_Diconnect()
        {

            emgReader.Dispose();
            emgStream.Dispose();
            emgSocket.Dispose();

            emgSW.Dispose();
            emgTimestampSW.Dispose();
        }

        public void StreamEMG(CancellationToken token, string saveDir, string datestamp)
        {
            // establish transmission
            streamStart_timestamp = DateTime.Now.Ticks;
            emgStream = emgSocket.GetStream();
            emgReader = new BinaryReader(emgStream);

            // setup logging
            
            string filename = currPart + "_RawFormattedEMGData_" + file_extension;
            string stamp_filename = currPart + "_TimestampEMG_" + file_extension;
            save_path = Path.Combine(saveDir, currPart, datestamp);
            if (calibrationOn)
            {
                save_path = Path.Combine(save_path, @"/" + "Calibration");
            }

            try
            {
                if (!Directory.Exists(save_path))
                {
                    System.IO.Directory.CreateDirectory(save_path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EMG Streamer, Directory Exception - " + ex.Message.ToString());
            }

            emgSW = new StreamWriter(Path.Combine(save_path, filename));
            string emgLog_label = "EMG1";
            for (int i = 1; i < numberOfChannels; i++)
            {
                emgLog_label = string.Join(",", emgLog_label, "EMG" + (i + 1).ToString());
            }
            emgLog_label = string.Join(",", emgLog_label, "Timestamp");
            emgSW.WriteLine(emgLog_label);
            emgSW.Flush();
            // timestamp log file could be used for easier interpolation of timestamps if needed
            // (since there are repeats of the same timestamp for about 25 samples)
            emgTimestampSW = new StreamWriter(Path.Combine(save_path, stamp_filename));
            emgTimestampSW.WriteLine("Timestamp of EMG bytes retrieved");
            emgTimestampSW.Flush();

            while (!token.IsCancellationRequested)
            {
                // beging streaming bytes from the API
                try
                {
                    byte[] sampleBuffer;
                    long formattedTimestamp;
                    int bytesAvailable = emgSocket.Available; // max EMG samples per frame is 27, equaling 1728 bytes. which is the fasted data can be received. 
                                                              // max data that can be held for transmission is 65536 bytes, 1024 samples, if not attempted to receive fast enough
                    float[] unpackedSamp = new float[numberOfChannels];

                    if (bytesAvailable > bytesPerSample)
                    {
                        sampleBuffer = new byte[bytesPerSample];
                        emgReader.Read(sampleBuffer, 0, bytesPerSample); // reads total bytes for each sample i.e. 64
                        formattedTimestamp = DateTime.Now.Ticks; // timestamps the bytes read


                        int indTracker = 0; // used to process the total bytes of all channels for each sample

                        // convert bytes to floating point values
                        unpackedSamp[0] = BitConverter.ToSingle(sampleBuffer.Skip(indTracker).Take(bytesPerChannel).ToArray(), 0);
                        indTracker = indTracker + 4;
                        string emgLog = unpackedSamp[0].ToString();
                        for (int i = 1; i < numberOfChannels; i++)
                        {
                            unpackedSamp[i] = BitConverter.ToSingle(sampleBuffer.Skip(indTracker).Take(bytesPerChannel).ToArray(), 0);
                            emgLog = string.Join(",", emgLog, unpackedSamp[i]);
                            indTracker = indTracker + 4;

                        }
                        if (logging)
                        {
                            emgLog = string.Join(",", emgLog, formattedTimestamp);
                            emgSW.WriteLine(emgLog);
                            emgTimestampSW.WriteLine(formattedTimestamp);

                        }

                        // add unpacked data to queue for prepping to plot
                        rawPacket rawSampBuff = new rawPacket(unpackedSamp, formattedTimestamp);
                        rawSamplesQueueForProcc.Add(rawSampBuff);
                        lock (plotDataLock)
                        {
                            rawSamplesQueueForPlot.Add(unpackedSamp); 
                        }

                    }
                    emgSW.Flush();
                    emgTimestampSW.Flush();
                }
                catch (Exception e)
                {
                    Console.WriteLine("EMG Streamer - ", e);
                }
            }
            // flush outside of while loop to avoid taking up processing time
            emgTimestampSW.Flush();
            emgSW.Flush();
        }

        // TO DO: change the following method to filter and log filtered and algo related info
        public void filtEMGstream(CancellationToken token)
        {
            string filename;
            string stamp_filename;
            if (calibrationOn)
            {
                filename = currPart + "_filtEMGData_" + file_extension;
                //string filenameFilt = @"\FilteredFormattedEMGData_" + file_extension;
                stamp_filename = currPart + "_TimestampFilt_" + file_extension;
                emgFiltSW = new StreamWriter(save_path + filename);
                emgFiltSW.WriteLine(string.Join(",", "emg channel", "filt signal", "signal timstamp"));
                
            }
            else
            {
                filename = @"\StimEMGData_" + file_extension;
                //string filenameFilt = @"\FilteredFormattedEMGData_" + file_extension;
                stamp_filename = @"\StimTimestamp_" + file_extension;
                emgFiltSW = new StreamWriter(save_path + filename);
                emgFiltSW.WriteLine(string.Join(",", "emg channel", "filt signal", "signal timstamp"));
                
                filename = @"\EnvData_" + file_extension;
                emgEnvelopedSW = new StreamWriter(save_path + filename);
                emgEnvelopedSW.WriteLine(string.Join(",", "emg channel", "enveloped signal", "start stim", "stim command", "movement detected", "movement detected timestamp", "percent", "threshold"));
            }
            
            
            //emgSW.WriteLine(string.Join(",", "emg channel", "raw signal", "filt signal", "signal timstamp", "movement detected", "movement detected timestamp"));

            //emgFiltSW = new StreamWriter(saveDir + filenameFilt);
            //emgFiltSW.WriteLine(string.Join(",", "emg channel", "channel signal", "timstamp"));

            //emgTimestampSW = new StreamWriter(saveDir + stamp_filename);
            //emgTimestampSW.WriteLine(string.Join(",", "retrieval timestamp of bytes as they become available", "number of bytes stored that became available"));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int rawSamplesAvailable = rawSamplesQueueForProcc.Count;
                    if (rawSamplesAvailable > 0)
                    {
                        rawPacket rawSampPacket = new rawPacket();
                        rawSampPacket = rawSamplesQueueForProcc.Take();
                        float[] rawSamples = rawSampPacket.samples;
                        long timestampForAllSamples = rawSampPacket.stamp;
                        float[] filtSamples = new float[numberOfChannels];
                        float[] envelopedSamples = new float[numberOfChannels];
                        // filter data
                        filtSamples = _processingMod.IIRFilter(rawSamples);
                        envelopedSamples = _processingMod.envelopeSignals(_stimMod.rectifySignals(filtSamples));
                        // FILE SAVED FOR CALIBRATION DATA VS STIM DATA ARE DIFFERENT, SINCE CALIBRATION DATA WILL NOT INCLUDE STIM VALUES
                        // ADD CHECK BOX TO UI INDICATE WHETHER CALIBRATION
                        // movement detection
                        // add raw, filtered, and movement detection to queue

                        // log data
                        if (calibrationOn)
                        {
                            rawSamplesQueue.Add(rawSamples);
                            filtSamplesQueue.Add(filtSamples);
                            for (int i = 0; i < filtSamples.Length; ++i)
                            {
                                emgFiltSW.WriteLine(string.Join(",", $"{i + 1}", rawSamples[i].ToString(), filtSamples[i].ToString(), timestampForAllSamples));
                            }
                        }
                        else
                        {
                            _stimMod.stimEnabled = _stimEnabled;

                            (int[] movementDetected, long[] movementDetectedTimestamp) = _stimMod.trigerStim(envelopedSamples, _stimMod.thresh);

                            _generateStim = _stimMod.generateStim;
                            //rawSamplesQueue.Add(emgSamples);
                            filtSamplesQueue.Add(filtSamples);
                            threshSamplesQueue.Add(_stimMod.thresh);
                            stimSamplesQueue.Add(movementDetected);

                            for (int i = 0; i < filtSamples.Length; ++i)
                            {
                                emgFiltSW.WriteLine(string.Join(",", $"{i + 1}", rawSamples[i].ToString(), filtSamples[i].ToString(), timestampForAllSamples));
                                emgEnvelopedSW.WriteLine(string.Join(",", $"{i + 1}", envelopedSamples[i].ToString(), _stimEnabled, _generateStim, movementDetected[i], movementDetectedTimestamp[i], _stimMod.percent, _stimMod.thresh[i]));
                            }
                        }
                    }
                }
                catch(Exception ex)
                {
                    Console.WriteLine("Filt function thread - " + ex.Message);
                }
            }
        }
        // TO DO: add another method for prepping filtered data for plotting
        public void prepRawForPlot(CancellationToken token)
        {

            // check how much data is available
            // check how much data is in the returned list for plotting (if it's empty, only return when it's no longer empty)
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // check how much data is available
                    int samplesAvailable = rawSamplesQueueForPlot.Count;

                    // for however much data is available, remove that much data from the beginning of the returned list
                    // turn all the available data into lists, concatenate that with the returned list,
                    if (samplesAvailable < numSamplesToPlot && samplesAvailable != 0)
                    {
                        // for every sample not available in the numSamplesToPlot
                        int numSamplesToBuffer = numSamplesToPlot - samplesAvailable;
                        List<float>[] buffer = new List<float>[numSamplesToBuffer];
                        buffer[0] = r_emgDataToPlot[numSamplesToPlot - numSamplesToBuffer];
                        int indTracker = 1;
                        lock (plotDataLock)
                        {
                            for (int b = numSamplesToPlot - numSamplesToBuffer + 1; b < numSamplesToPlot; b++)
                            {
                                buffer[indTracker] =r_emgDataToPlot[b];
                                indTracker++;
                            }
                            for (int b = 0; b < numSamplesToBuffer; b++)
                            {
                                r_emgDataToPlot[b] = buffer[b];
                            }
                            for (int i = numSamplesToBuffer; i < numSamplesToPlot; i++)
                            {
                                float[] data = rawSamplesQueueForPlot.Take();
                                r_emgDataToPlot[i] = new List<float>(data);
                            }
                        }

                    }
                    else if (samplesAvailable >= numSamplesToPlot)
                    {
                        lock (plotDataLock)
                        {
                            for (int i = 0; i < numSamplesToPlot; i++)
                            {
                                float[] data = rawSamplesQueueForPlot.Take();
                                r_emgDataToPlot[i] = new List<float>(data);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Prep for Flot - " + e.Message);
                }
            }
        }

        public void prepFiltForPlot(CancellationToken token)
        {

            // check how much data is available
            // check how much data is in the returned list for plotting (if it's empty, only return when it's no longer empty)
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // check how much data is available
                    int samplesAvailable = filtSamplesQueue.Count;

                    // for however much data is available, remove that much data from the beginning of the returned list
                    // turn all the available data into lists, concatenate that with the returned list,
                    if (samplesAvailable < numSamplesToPlot && samplesAvailable != 0)
                    {
                        // for every sample not available in the numSamplesToPlot
                        int numSamplesToBuffer = numSamplesToPlot - samplesAvailable;
                        List<float>[] buffer = new List<float>[numSamplesToBuffer];
                        buffer[0] = f_emgDataToPlot[numSamplesToPlot - numSamplesToBuffer];
                        int indTracker = 1;
                        lock (plotFiltLock)
                        {
                            for (int b = numSamplesToPlot - numSamplesToBuffer + 1; b < numSamplesToPlot; b++)
                            {
                                buffer[indTracker] = f_emgDataToPlot[b];
                                indTracker++;
                            }
                            for (int b = 0; b < numSamplesToBuffer; b++)
                            {
                                f_emgDataToPlot[b] = buffer[b];
                            }
                            for (int i = numSamplesToBuffer; i < numSamplesToPlot; i++)
                            {
                                float[] data = filtSamplesQueue.Take();
                                f_emgDataToPlot[i] = new List<float>(data);
                            }
                        }

                    }
                    else if (samplesAvailable >= numSamplesToPlot)
                    {
                        lock (plotFiltLock)
                        {
                            for (int i = 0; i < numSamplesToPlot; i++)
                            {
                                float[] data = filtSamplesQueue.Take();
                                f_emgDataToPlot[i] = new List<float>(data);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Prep for Flot - " + e.Message);
                }
            }
        }
        public List<float>[] getRawData()
        {
            List<float>[] emgDataToPlotBuffer = new List<float>[numSamplesToPlot];
            lock (plotDataLock)
            {
                r_emgDataToPlot.CopyTo(emgDataToPlotBuffer, 0);
            }
            return emgDataToPlotBuffer;
        }
        // TO DO: this needs to be implemented
        public List<float>[] getFiltData()
        {
            List<float>[] plotFiltData = plotFiltDataQueue.Take();
            return plotFiltData; // this would return null if rawSamplesQueue.Count <=0 ---> needs to be fixed
        }
        // TO DO:
        public (List<double>[] thresh, List<int>[] stim) getMovementStimData()
        {
            List<double>[] plotThreshData = plotThreshDataQueue.Take();
            List<int>[] plotStimData = plotStimDataQueue.Take();

            return (plotThreshData, plotStimData);
        }

        private double elapsedTime(long sampleStamp)
        {
            double elapsed_time_per_sample;
            elapsed_time_per_sample = (sampleStamp - streamStart_timestamp) * 100 % 1e9 / 1e9;

            return elapsed_time_per_sample;

        }
    }
}
