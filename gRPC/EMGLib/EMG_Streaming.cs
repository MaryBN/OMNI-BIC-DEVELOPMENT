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
        private int numSamplesToPlot = 2000;
        private List<float>[] rawData;
        private List<float>[] filtData;
        private List<double>[] threshData;
        private List<int>[] stimData;

        public bool streamingStatus = false;

        StreamWriter emgSW;
        StreamWriter emgFiltSW;
        StreamWriter timestampSW;

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

        private static long streamStart_timestamp;

        // calibration bool, if true stim is disabled, if false stim is enabled
        private bool calibrationOn = false;

        public EMG_Streaming()
        { 
            bytesPerSample = numberOfChannels * bytesPerChannel;
            rawData = new List<float>[numSamplesToPlot];
            filtData = new List<float>[numSamplesToPlot];
            threshData = new List<double>[numSamplesToPlot];
            stimData = new List<int>[numSamplesToPlot];

            _processingMod = new Processing_Modules(numberOfChannels);
            _stimMod = new Stim_Modules(numberOfChannels);
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

        }

        public void StreamEMG(CancellationToken token)
        {
            // establish transmission
            streamStart_timestamp = DateTime.Now.Ticks;
            emgStream = emgSocket.GetStream();
            emgReader = new BinaryReader(emgStream);

            //int sampleCounter = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    byte[] sampleBuffer;
                    long formattedTimestamp;
                    int bytesAvailable = emgSocket.Available; // max EMG samples per frame is 27, equaling 1728 bytes. which is the fasted data can be received. //
                                                              // max data that can be held for transmission is 65536 bytes, 1024 samples, if not attempted to receive fast enough
                    if (bytesAvailable > 0)
                    {
                        sampleBuffer = new byte[bytesAvailable];
                        emgReader.Read(sampleBuffer, 0, bytesAvailable); // reads all bytes that are available
                        formattedTimestamp = DateTime.Now.Ticks; // timestamps the bytes read

                        bytesBufferQueue.Add(sampleBuffer);
                        originalTimestampQueue.Add(formattedTimestamp);
                        bytesAvailableQueue.Add(bytesAvailable);
                    }


                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }

        public void unpackEMGstream(CancellationToken token, string saveDir, bool calibrating)
        {
            calibrationOn = calibrating;
            string file_extension = $"{DateTime.Now:yyyy - MM - dd_HH - mm - ss}.csv";
            string filename;
            string stamp_filename;
            if (calibrationOn)
            {
                filename = @"\CalibrationEMGData_" + file_extension;
                //string filenameFilt = @"\FilteredFormattedEMGData_" + file_extension;
                stamp_filename = @"\CalibrationTimestamp_" + file_extension;
                emgSW = new StreamWriter(saveDir + filename);
                emgSW.WriteLine(string.Join(",", "emg channel", "raw signal", "filt signal", "signal timstamp"));
                
            }
            else
            {
                filename = @"\StimEMGData_" + file_extension;
                //string filenameFilt = @"\FilteredFormattedEMGData_" + file_extension;
                stamp_filename = @"\StimTimestamp_" + file_extension;
                emgSW = new StreamWriter(saveDir + filename);
                emgSW.WriteLine(string.Join(",", "emg channel", "raw signal", "filt signal", "signal timstamp", "movement detected", "movement detected timestamp", "percent", "threshold"));
                
            }
            
            
            //emgSW.WriteLine(string.Join(",", "emg channel", "raw signal", "filt signal", "signal timstamp", "movement detected", "movement detected timestamp"));

            //emgFiltSW = new StreamWriter(saveDir + filenameFilt);
            //emgFiltSW.WriteLine(string.Join(",", "emg channel", "channel signal", "timstamp"));

            timestampSW = new StreamWriter(saveDir + stamp_filename);
            timestampSW.WriteLine(string.Join(",", "retrieval timestamp of bytes as they become available", "number of bytes stored that became available"));
            while (!token.IsCancellationRequested)
            {
                while (bytesBufferQueue.Count > 0)
                {

                    byte[] bufferBytes;
                    long timestampForAllSamples;
                    int bytesStoredinQueue;
                    List<float> emgSampList = new List<float>();
                    float[] emgSamples = new float[numberOfChannels];
                    string[] timestampAllChannels = new string[numberOfChannels];
                    double[] elapsed_time = new double[numberOfChannels];


                    bufferBytes = bytesBufferQueue.Take();
                    timestampForAllSamples = originalTimestampQueue.Take();
                    bytesStoredinQueue = bytesAvailableQueue.Take();

                    timestampSW.WriteLine(string.Join(",", timestampForAllSamples, bytesStoredinQueue));
                    float[] bufferFormattedData = new float[bytesStoredinQueue / bytesPerChannel];
                    int indTracker = 0;
                    for (int i = 0; i < bytesStoredinQueue / bytesPerChannel; i++)
                    {
                        byte[] byteBuffer;
                        byteBuffer = bufferBytes.Skip(indTracker).Take(bytesPerChannel).ToArray();
                        bufferFormattedData[i] = BitConverter.ToSingle(byteBuffer, 0);
                        indTracker = indTracker + 4;
                    }
                    // for every sample: bytesStored/(4 bytes * 14 ch)
                    indTracker = 0;
                    while (indTracker < bufferFormattedData.Count())
                    {
                        float[] filtSamples = new float[numberOfChannels];

                        // extract value for every channel
                        for (int i = 0; i < numberOfChannels; ++i)
                        {

                            emgSamples[i] = bufferFormattedData[indTracker];
                            emgSampList.Add(emgSamples[i]);
                            //timestampAllChannels[i] = timestampForAllSamples.ToString();
                            indTracker++;
                        }
                        // filter data
                        filtSamples = _processingMod.IIRFilter(emgSamples);

                        // FILE SAVED FOR CALIBRATION DATA VS STIM DATA ARE DIFFERENT, SINCE CALIBRATION DATA WILL NOT INCLUDE STIM VALUES
                        // ADD CHECK BOX TO UI INDICATE WHETHER CALIBRATION
                        // movement detection
                        // add raw, filtered, and movement detection to queue

                        // log data
                        if (calibrationOn)
                        {
                            rawSamplesQueue.Add(emgSamples);
                            filtSamplesQueue.Add(filtSamples);
                            for (int i = 0; i < filtSamples.Length; ++i)
                            {
                                emgSW.WriteLine(string.Join(",", $"{i + 1}", emgSamples[i].ToString(), filtSamples[i].ToString(), timestampForAllSamples));
                            }
                        }
                        else
                        {
                            (int[] movementDetected, long[] movementDetectedTimestamp) = _stimMod.trigerStim(_stimMod.rectifySignals(filtSamples), _stimMod.thresh);

                            rawSamplesQueue.Add(emgSamples);
                            filtSamplesQueue.Add(filtSamples);
                            threshSamplesQueue.Add(_stimMod.thresh);
                            stimSamplesQueue.Add(movementDetected);

                            for (int i = 0; i < filtSamples.Length; ++i)
                            {
                                emgSW.WriteLine(string.Join(",", $"{i + 1}", emgSamples[i].ToString(), filtSamples[i].ToString(), timestampForAllSamples, movementDetected[i] , movementDetectedTimestamp[i], _stimMod.percent, _stimMod.thresh[i]));
                            }
                        }
                        

                    }

                }
            }
        }
        public void prepForPlot(CancellationToken token)
        {
            // check how much data is available
            int samplesAvailable = rawSamplesQueue.Count;
            // check how much data is in the returned list for plotting (if it's empty, only return when it's no longer empty)
            while (!token.IsCancellationRequested)
            {
                if (rawData[0] == null)
                {
                    if (samplesAvailable < numSamplesToPlot)
                    {
                        int zeroVal = numSamplesToPlot - samplesAvailable;
                        Console.WriteLine("not enough data is available yet");
                        for (int i = 0; i < zeroVal; i++)
                        {
                            float[] raw = new float[numberOfChannels];
                            float[] filt = new float[numberOfChannels];
                            double[] thresh = new double[numberOfChannels];
                            int[] stim = new int[numberOfChannels];

                            for (int ch = 0; ch < numberOfChannels; ch++)
                            {
                                raw[ch] = 0f;
                                filt[ch] = 0f;
                                thresh[ch] = 0f;
                                stim[ch] = 0;
                            }
                            rawData[i] = new List<float>(raw);
                            filtData[i] = new List<float>(filt);
                            if (!calibrationOn)
                            {
                                
                                threshData[i] = new List<double>(thresh);
                                stimData[i] = new List<int>(stim);
                            }
                        }
                        for (int i = zeroVal; i < numSamplesToPlot; i++)
                        {
                            float[] raw = rawSamplesQueue.Take();
                            float[] filt = filtSamplesQueue.Take();
                            rawData[i] = new List<float>(raw);
                            filtData[i] = new List<float>(filt);
                            if (!calibrationOn)
                            {
                                double[] thresh = threshSamplesQueue.Take();
                                int[] stim = stimSamplesQueue.Take();
                                threshData[i] = new List<double>(thresh);
                                stimData[i] = new List<int>(stim);
                            }
                        }
                    }
                    if (samplesAvailable >= numSamplesToPlot)
                    {
                        for (int i = 0; i < numSamplesToPlot; i++)
                        {
                            float[] raw = rawSamplesQueue.Take();
                            float[] filt = filtSamplesQueue.Take();
                            rawData[i] = new List<float>(raw);
                            filtData[i] = new List<float>(filt);
                            if (!calibrationOn)
                            {
                                double[] thresh = threshSamplesQueue.Take();
                                int[] stim = stimSamplesQueue.Take();
                                threshData[i] = new List<double>(thresh);
                                stimData[i] = new List<int>(stim);
                            }
                        }
                    }

                }
                else
                {
                    // for however much data is available, remove that much data from the beginning of the returned list
                    // turn all the available data into lists, concatenate that with the returned list,

                    if (samplesAvailable <= numSamplesToPlot)
                    {
                        for (int i = 0; i < samplesAvailable; i++)
                        {
                            // remove rawData[0-samplesAvailable]
                            rawData[i].Clear();
                            filtData[i].Clear();
                            if (!calibrationOn)
                            {
                                threshData[i].Clear();
                                stimData[i].Clear();
                            }
                        }
                        for (int i = samplesAvailable; i < numSamplesToPlot; i++)
                        {
                            // add rawData[samplesAvailable-numSamplesToPlot]
                            float[] raw = rawSamplesQueue.Take();
                            float[] filt = filtSamplesQueue.Take();
                            rawData[i] = new List<float>(raw);
                            filtData[i] = new List<float>(filt);
                            if (!calibrationOn)
                            {
                                double[] thresh = threshSamplesQueue.Take();
                                int[] stim = stimSamplesQueue.Take();
                                threshData[i] = new List<double>(thresh);
                                stimData[i] = new List<int>(stim);
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < numSamplesToPlot; i++)
                        {
                            float[] raw = rawSamplesQueue.Take();
                            float[] filt = filtSamplesQueue.Take();
                            rawData[i] = new List<float>(raw);
                            filtData[i] = new List<float>(filt);
                            if (!calibrationOn)
                            {
                                double[] thresh = threshSamplesQueue.Take();
                                int[] stim = stimSamplesQueue.Take();
                                threshData[i] = new List<double>(thresh);
                                stimData[i] = new List<int>(stim);
                            }
                        }
                    }
                }
                plotRawDataQueue.Add(rawData);
                plotFiltDataQueue.Add(filtData);
                if (!calibrationOn)
                {
                    plotThreshDataQueue.Add(threshData);
                    plotStimDataQueue.Add(stimData);
                }
            }


        }

        public List<float>[] getRawData()
        {
            List<float>[] plotRawData = plotRawDataQueue.Take();
            return plotRawData; // this would return null if rawSamplesQueue.Count <=0 ---> needs to be fixed
        }
        public List<float>[] getFiltData()
        {
            List<float>[] plotFiltData = plotFiltDataQueue.Take();
            return plotFiltData; // this would return null if rawSamplesQueue.Count <=0 ---> needs to be fixed
        }

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
