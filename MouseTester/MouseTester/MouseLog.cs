using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MouseTester.Diagnostics;

namespace MouseTester
{
    public class MouseLog
    {
        private string desc = "MouseTester";
        private double cpi = 400.0;
        private List<MouseEvent> events = new List<MouseEvent>();
        public List<ColdSample> ColdSamples = new List<ColdSample>();
        public List<EtwAnomaly> EtwAnomalies = new List<EtwAnomaly>();
        public List<CauseAttribution> Attributions = new List<CauseAttribution>();
        public List<KeyValuePair<string, long>> TopDpcSources = new List<KeyValuePair<string, long>>();
        public List<KeyValuePair<string, long>> TopIsrSources = new List<KeyValuePair<string, long>>();
        public long TotalDpc;
        public long TotalIsr;
        public TrustReport Trust;
        public long QpcFrequency;
        public bool EtwWasActive;
        public string EtwError;
        public bool RecordedAsAdmin;
        public uint TimerResolutionDuringRecording100ns;

        public double Cpi 
        { 
            get 
            { 
                return this.cpi; 
            } 
            set
            { 
                cpi = value; 
            }
        }

        public string Desc
        {
            get
            {
                return this.desc;
            }
            set
            {
                this.desc = value;
            }
        }

        public List<MouseEvent> Events
        {
            get
            {
                return this.events;
            }
        }

        public void Add(MouseEvent e)
        {
            this.events.Add(e);
        }

        public void Clear()
        {
            this.events.Clear();
            this.ColdSamples.Clear();
            this.EtwAnomalies.Clear();
            this.Attributions.Clear();
            this.TopDpcSources.Clear();
            this.TopIsrSources.Clear();
            this.TotalDpc = 0;
            this.TotalIsr = 0;
            this.Trust = null;
            this.EtwWasActive = false;
            this.EtwError = null;
            this.RecordedAsAdmin = false;
            this.TimerResolutionDuringRecording100ns = 0;
        }

        public void Load(string fname)
        {
            this.Clear();

            try
            {
                using (StreamReader sr = File.OpenText(fname))
                {
                    this.desc = sr.ReadLine();
                    this.cpi = double.Parse(sr.ReadLine());
                    string headerline = sr.ReadLine();
                    while (sr.Peek() > -1)
                    {
                        string line = sr.ReadLine();
                        string[] values = line.Split(',');
                        if (values.Length == 4)
                        {
                            this.Add(new MouseEvent(ushort.Parse(values[3]), int.Parse(values[0]), int.Parse(values[1]), double.Parse(values[2])));
                        }
                        else if (values.Length == 3)
                        {
                            this.Add(new MouseEvent(0, int.Parse(values[0]), int.Parse(values[1]), double.Parse(values[2])));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }

        public void Save(string fname)
        {
            try
            {
                using (StreamWriter sw = File.CreateText(fname))
                {
                    sw.WriteLine(this.desc);
                    sw.WriteLine(this.cpi.ToString());
                    sw.WriteLine("xCount,yCount,Time (ms),buttonflags");
                    foreach (MouseEvent e in this.events)
                    {
                        sw.WriteLine(e.lastx.ToString() + "," + e.lasty.ToString() + "," + e.ts.ToString(CultureInfo.InvariantCulture) + "," + e.buttonflags.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }
        
        public int deltaX()
        {
            return this.events.Sum(e => e.lastx);
        }

        public int deltaY()
        {
            return this.events.Sum(e => e.lasty);
        }

        public double path()
        {
            return this.events.Sum(e => Math.Sqrt((e.lastx * e.lastx) + (e.lasty * e.lasty)));
        }
    }
}
