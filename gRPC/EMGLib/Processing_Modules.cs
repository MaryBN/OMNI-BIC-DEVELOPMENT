using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace EMGLib
{
    public partial class Processing_Modules
    {
        private int numChannels;
        private List<float>[] prevInput; // initial previous inputs (zero-padding)
        private List<float>[] prevFiltOut; // initial previous outputs (zero-padding)

        // Filter coefficients: **currently copied from bandpass butterworth from python**
        private List<float> b = new List<float> { 0.231f, 0f, -0.4626f, 0f, 0.231f }; // numerator coefficients
        private List<float> a = new List<float> { 1f, -2.14f, 1.553f, -0.592f, 0.1834f }; // denominator coefficients
        private float gainVal = 0.2313f;

        

        public Processing_Modules(int channels)
        {
            numChannels = channels;
            prevInput = new List<float>[numChannels];
            prevFiltOut = new List<float>[numChannels];

            

            // create input and output array for filter
            for (int i = 0; i < numChannels; i++)
            {
                prevInput[i] = new List<float> { 0f, 0f, 0f, 0f };
                prevFiltOut[i] = new List<float> { 0f, 0f, 0f, 0f };
            }
            
        }
        public float[] IIRFilter(float[] currSamp)
        {
            float[] filtTemp = new float[16];

            for (int i = 0; i < 16; i++)
            {
                // 2nd order IIR filter
                //if(currSamp[i] != 0f)
                //{
                //    Console.WriteLine("test");
                //}
                filtTemp[i] = (gainVal * b[0] * currSamp[i] + gainVal * b[1] * prevInput[i][0] + gainVal * b[2] * prevInput[i][1] + gainVal * b[3] * prevInput[i][2] + gainVal * b[4] * prevInput[i][3]
                    - a[1] * prevFiltOut[i][0] - a[2] * prevFiltOut[i][1] - a[3] * prevFiltOut[i][2] - a[4] * prevFiltOut[i][3]);

                prevFiltOut[i].Insert(0, filtTemp[i]);
                prevFiltOut[i].RemoveAt(prevFiltOut[i].Count - 1);

                // Store most recent sample at the beginning of history window
                prevInput[i].Insert(0, currSamp[i]);
                prevInput[i].RemoveAt(prevInput[i].Count - 1);
            }


            return filtTemp;
        }
    }
}
