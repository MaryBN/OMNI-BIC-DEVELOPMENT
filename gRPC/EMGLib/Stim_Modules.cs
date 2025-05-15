﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EMGLib
{
    public class Stim_Modules
    {
        public bool stimEnabled = false;
        public bool generateStim = false;
        public float percent;
        int numberOfChannels;

        // Calibration and threshold related
        public float[] maxSig;
        public double[] thresh;

        public Stim_Modules(int numChannels)
        {
            numberOfChannels = numChannels;
            maxSig = new float[numChannels];
            thresh = new double[numChannels];
        }

        public void setThresh()
        {
            // calculate threshold for each channel
            for (int ch = 0; ch < numberOfChannels; ch++)
            {
                thresh[ch] = maxSig[ch] * percent / 100;
                Console.WriteLine("max sig/thresh: " + maxSig[ch].ToString() 
                    + "/" + thresh[ch].ToString());
            }
        }

        public float[] rectifySignals(float[] signal)
        {
            float[] rectifiedSignal = new float[signal.Length];
            for (int ch =0; ch < signal.Length; ch++)
            {
                rectifiedSignal[ch] = Math.Abs(signal[ch]);
            }
            return rectifiedSignal;
        }

        public (int[] movementDetected, long[] movementDetectedTimestamp) trigerStim(float[] signal, double[] thresh)
        {
            // movement not detected = 0, movement detected = 1
            int[] stimulate = new int[thresh.Length];
            long[] movementDetectedTimestamp = new long[thresh.Length];
            for (int ch = 0; ch < thresh.Length; ch++)
            {
                if (signal[ch] >= thresh[ch] & signal[ch] != 0)
                {
                    // timestamp for when signal above threshold was detected
                    movementDetectedTimestamp[ch] = DateTime.Now.Ticks;
                    stimulate[ch] = 1;

                    if (stimEnabled)
                    {
                        generateStim = true;
                    }
                    else
                    {
                        generateStim = false;
                    }
                    
                }
                else
                {
                    movementDetectedTimestamp[ch] = DateTime.Now.Ticks;
                    stimulate[ch] = 0;

                    generateStim = false;
                }
            }
            
            return (stimulate, movementDetectedTimestamp);
        }
    }
}
