using MouseTester.Diagnostics;

namespace MouseTester
{
    public class MouseEvent
    {
        public ushort buttonflags;
        public int lastx;
        public int lasty;
        public long pcounter;
        public double ts;
        public EventDiagnostics diag;

        public MouseEvent(ushort buttonflags, int lastx, int lasty, long pcounter)
        {
            this.buttonflags = buttonflags;
            this.lastx = lastx;
            this.lasty = lasty;
            this.pcounter = pcounter;
            this.ts = 0.0;
        }
        public MouseEvent(ushort buttonflags, int lastx, int lasty, double ts)
        {
            this.buttonflags = buttonflags;
            this.lastx = lastx;
            this.lasty = lasty;
            this.pcounter = 0;
            this.ts = ts;
        }

    }
}
