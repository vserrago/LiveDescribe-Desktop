﻿using LiveDescribe.Extensions;
using LiveDescribe.Factories;
using LiveDescribe.Model;
using LiveDescribe.Resources.UiStrings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace LiveDescribe.Utilities
{
    public class AudioUtility
    {
        #region Logger
        private static readonly log4net.ILog Log = log4net.LogManager.GetLogger
            (MethodBase.GetCurrentMethod().DeclaringType);
        #endregion

        private readonly string _videoFile;
        private readonly string _audioFile;
        private readonly Header _header;

        public AudioUtility(Project p)
        {
            _videoFile = p.Files.Video;
            _audioFile = Path.Combine(p.Folders.Cache, Path.GetFileNameWithoutExtension(_videoFile) + ".wav");
            _header = new Header();
        }

        #region Getter / Setter
        public Header Header
        {
            get { return _header; }
        }
        #endregion

        /// <summary>
        /// Strips the audio from the given video file and outputs a wave file using FFMPEG.
        /// </summary>
        /// <remarks>This function uses the executable FFMPEG file to handle the audio stripping</remarks>
        /// <param name="reportProgressWorker">
        /// Used to determine what the current progress of stripping the audio is
        /// </param>
        public void StripAudio(BackgroundWorker reportProgressWorker)
        {
            Log.Info("Preparing to strip audio from video");
            //gets the path of the ffmpeg.exe file within the LiveDescribe solution
            var appDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string ffmpegPath = Path.Combine(appDirectory, "Utilities/_ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
            {
                MessageBoxFactory.ShowError(string.Format(UiStrings.Error_Format_FfmpegNotFound, ffmpegPath));
                Log.Error("ffmpeg.exe can not be found at " + ffmpegPath);
                return;
            }

            //the parameters that are sent to the ffmpeg command
            string strParam = " -i \"" + _videoFile + "\" -ac 2 -ab 160k -ar 44100 -f wav -vn -y \"" + _audioFile + "\"";

            var ffmpeg = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    FileName = ffmpegPath,
                    Arguments = strParam,
                    CreateNoWindow = true
                }
            };

            ffmpeg.Start();

            #region parse ffmpeg
            double totalTime = 0;
            double currentTime = 0;
            //stream reader used to parse the output of the ffmpeg process
            StreamReader input = ffmpeg.StandardError;

            Log.Info("Attempting to parse ffmpeg output");
            /* Parsing the output of ffmpeg to obtain the total time and the current time
             to  calculate a percentage whose value is used to update the progress bar*/
            try
            {
                while (!input.EndOfStream)
                {
                    string text = input.ReadLine();
                    string word = "";
                    for (int i = 0; i < text.Length; i++)
                    {
                        word += text[i];
                        if (text[i] == ' ')
                        {
                            if (word.Equals("Duration: "))
                            {
                                int currentIndex = i + 1;
                                string time = "";

                                for (int j = currentIndex; j < currentIndex + 11; j++)
                                {
                                    time += text[j];
                                }

                                totalTime = GetTime(time);
                            }
                            word = "";
                        }

                        if (text[i] == '=')
                        {
                            if (word.Equals("time="))
                            {
                                int currentIndex = i + 1;
                                string time = "";

                                for (int j = currentIndex; j < currentIndex + 11; j++)
                                {
                                    time += text[j];
                                }

                                currentTime = GetTime(time);
                            }
                        }
                    }

                    //updates the progress bar given that the total time is not zero
                    if (totalTime != 0)
                    {
                        int percentComplete = Convert.ToInt32((currentTime / totalTime) * 100);
                        if (percentComplete <= 100)
                        {
                            reportProgressWorker.ReportProgress(percentComplete);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBoxFactory.ShowError(ex.Message);
                Log.Error("An error occured during ffmpeg audio stripping", ex);
            }
            #endregion

            ffmpeg.WaitForExit();

            var fileInfo = new FileInfo(_audioFile);
            Log.Info("Audio file length: " + fileInfo.Length);
        }

        /// <summary>
        /// Convert from ffmpeg time to seconds
        /// </summary>
        /// <param name="time">the time in ffmpeg format HH:MM:SS</param>
        /// <returns></returns>
        private double GetTime(string time)
        {
            double hours = Convert.ToDouble(time.Substring(0, 2)) * 60 * 60;
            double minutes = Convert.ToDouble(time.Substring(3, 2)) * 60;
            double seconds = Convert.ToDouble(time.Substring(6, 2));

            return hours + minutes + seconds;
        }


        /// <summary>
        /// Read the wav data from the stripped audio and return the sample data
        /// </summary>
        /// <param name="reportProgressWorker">
        /// Used to determine what the current progress of stripping the audio is
        /// </param>
        /// <returns>data</returns>
        public List<short> ReadWavData(BackgroundWorker reportProgressWorker)
        {
            var data = new List<short>();

            Log.Info("Opening .wav file for reading at " + _audioFile);
            using (var fs = new FileStream(_audioFile, FileMode.Open, FileAccess.Read))
            using (var r = new BinaryReader(fs))
            {
                Log.Info("Reading header info from .wav file");

                ReadHeaderData(r);

                int ratio = _header.Channels == 2 ? 40 : 80;

                Log.Info("Reading sound data from .wav file");
                for (int i = 0; i < _header.DataSize; i += ratio)
                {
                    data.Add(r.ReadInt16());
                    //Skip the next n-2 bytes
                    r.ReadBytes(ratio - 2);
                }

                Log.Info("Sound data successfully read in from .wav file");
                return data;
            }
        }

        /// <summary>
        /// Reads the wave header from the binary stream, and checks to see that the data is
        /// correct and that the binary reader's stream is at the right position.
        /// </summary>
        /// <param name="r">The binary reader stream containing wave file data.</param>
        private void ReadHeaderData(BinaryReader r)
        {
            var riffFileDescriptor = r.ReadChars(4);
            ValidateHeaderData(riffFileDescriptor, Header.RiffFileDescriptor);

            _header.FileSize = r.ReadUInt32();
            var fileTypeHeader = r.ReadChars(4);
            ValidateHeaderData(fileTypeHeader, Header.FileTypeHeader);

            var formatChunkMarker = r.ReadChars(4);
            ValidateHeaderData(formatChunkMarker, Header.FormatChunkMarker);

            uint formatChunkSize = r.ReadUInt32();

            ushort audioFormat = r.ReadUInt16();
            if (audioFormat != Header.AudioFormat)
                throw new InvalidDataException("Audio format is incorrect: " + audioFormat);

            _header.Channels = r.ReadUInt16();
            _header.SampleRate = r.ReadUInt32();
            _header.ByteRate = r.ReadUInt32();
            _header.BlockAlign = r.ReadUInt16();
            _header.BitsPerSample = r.ReadUInt16();

            /* There is a chance that the wave file will have a few extra bytes in its header. If
             * it does, then calculate how many and skip over them.
             */
            int extaFormatBytes = (Header.DefaultFormatChunkSize < formatChunkSize)
                ? (int)(formatChunkSize - Header.DefaultFormatChunkSize)
                : 0;

            r.ReadBytes(extaFormatBytes);

            var dataChunkMarker = r.ReadChars(4);
            ValidateHeaderData(dataChunkMarker, Header.DataChunkMarker);

            _header.DataSize = r.ReadUInt32();

            if (r.BaseStream.Position != Header.DefaultHeaderByteSize + extaFormatBytes)
                throw new InvalidDataException("Stream is at the wrong position");
        }

        private static void ValidateHeaderData(char[] data, char[] other)
        {
            if (!data.ElementsEquals(other))
                throw new InvalidDataException("Data is invalid: " + data);
        }

        /// <summary>
        /// Will normalized the data using a linear transformation using the following formula: I(n)
        /// = [(I(n) - min) newMax - newMin / max - min] + newMin
        /// </summary>
        /// <param name="data">data</param>
        /// <param name="newMin">newMin</param>
        /// <param name="newMax">newMax</param>
        /// <param name="oldMin">oldMin</param>
        /// <param name="oldMax">oldMax</param>
        /// <returns></returns>
        private List<short> NormalizeData(List<short> data, float newMin, float newMax, float oldMin, float oldMax)
        {
            for (int dataPoint = 0; dataPoint < data.Count; dataPoint++)
            {
                float multFactor = (newMax - newMin) / (oldMax - oldMin);
                data[dataPoint] = (short)(((data[dataPoint] - oldMin) * multFactor) + newMin);
            }
            return data;
        }

        /// <summary>
        /// Calculates the Root Mean Square of the audio data
        /// </summary>
        /// <param name="data">data</param>
        /// <returns>root mean square</returns>
        private float Rms(List<short> data)
        {
            float total = 0;
            const float mid = 0.5F;
            for (int i = 0; i < data.Count; i++)
            {
                total += (float)Math.Pow(data[i] - mid, 2);
            }

            return (float)Math.Sqrt(total / data.Count);
        }

        /// <summary>
        /// Attempts to delete the file created by stripping the audio.
        /// </summary>
        public void DeleteAudioFile()
        {
            Log.Info("Deleting audio .wav file");
            try
            {
                File.Delete(_audioFile);
            }
            catch (Exception e)
            {
                Log.Warn("An error occured while trying to delete the audio file", e);
            }

            Log.Info(!File.Exists(_audioFile) ? "Audio .wav file deleted" : "Audio .wav file NOT deleted");
        }
    }
}
