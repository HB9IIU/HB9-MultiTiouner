using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace QocastPlayer.Receiver
{
    internal sealed class ScanCarrier
    {
        public double FrequencyMhz;
        public uint SymbolRateKsps;
        public double Strength;
    }

    // Backend owns this connection: closing the browser does not stop scanning.
    internal sealed class BatcMonitor : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly object _sync = new object();
        private readonly Task _task;
        private ushort[] _samples;
        private DateTime _received;
        private long _version;
        public BatcMonitor() { _task = Task.Run(ReadLoop); }
        public bool Snapshot(out ushort[] samples, out long version)
        {
            lock (_sync) { samples = _samples; version = _version; return samples != null && DateTime.UtcNow - _received < TimeSpan.FromSeconds(5); }
        }
        private async Task ReadLoop()
        {
            while (!_cancel.IsCancellationRequested)
            {
                try
                {
                    using (var socket = new ClientWebSocket())
                    {
                        socket.Options.AddSubProtocol("fft");
                        await socket.ConnectAsync(new Uri("wss://eshail.batc.org.uk/wb/fft"), _cancel.Token);
                        var buffer = new byte[262144];
                        while (!_cancel.IsCancellationRequested && socket.State == WebSocketState.Open)
                        {
                            int length = 0; WebSocketReceiveResult result;
                            do
                            {
                                if (length == buffer.Length) throw new InvalidOperationException("BATC frame too large");
                                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), _cancel.Token);
                                length += result.Count;
                            } while (!result.EndOfMessage);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            if (result.MessageType != WebSocketMessageType.Binary || length < 64 || length % 2 != 0) continue;
                            var samples = new ushort[length / 2];
                            for (int i=0;i<samples.Length;i++) samples[i]=(ushort)(buffer[2*i] | buffer[2*i+1]<<8);
                            lock (_sync) { _samples=samples; _received=DateTime.UtcNow; _version++; }
                        }
                    }
                }
                catch (Exception ex)
                { Serilog.Log.Debug(ex, "BATC scanner feed unavailable"); }
                try { await Task.Delay(2000, _cancel.Token); } catch (OperationCanceledException) { }
            }
        }
        public void Dispose() { _cancel.Cancel(); _task.Wait(3000); }

        // Same fitted-floor detector as SDR-Tiouner's auto_monitor.h.
        public static List<ScanCarrier> Detect(ushort[] fft, out bool beaconVisible)
        {
            beaconVisible=false;
            var found=new List<ScanCarrier>();
            if(fft==null || fft.Length<32)return found;
            int n=fft.Length;double step=9.0/(n-1);
            var sm=new double[n];
            for(int i=0;i<n;i++){int count=0;for(int j=Math.Max(0,i-3);j<=Math.Min(n-1,i+3);j++){sm[i]+=fft[j];count++;}sm[i]/=count;}
            double median=sm.OrderBy(x=>x).ElementAt(n/2);
            double[] c={median,0,0};bool[] keep=sm.Select(x=>x<=median).ToArray();
            for(int pass=0;pass<5;pass++)
            {
                double[,] m=new double[3,4];int used=0;
                for(int i=0;i<n;i++)if(keep[i]){used++;double x=(double)i/(n-1);double[] p={1,x,x*x};for(int r=0;r<3;r++){for(int k=0;k<3;k++)m[r,k]+=p[r]*p[k];m[r,3]+=p[r]*sm[i];}}
                if(used<16)break;bool ok=true;
                for(int col=0;col<3;col++)
                {
                    int pivot=col;for(int r=col+1;r<3;r++)if(Math.Abs(m[r,col])>Math.Abs(m[pivot,col]))pivot=r;
                    if(Math.Abs(m[pivot,col])<1e-12){ok=false;break;}
                    for(int k=0;k<4;k++){double temp=m[col,k];m[col,k]=m[pivot,k];m[pivot,k]=temp;}
                    for(int r=0;r<3;r++)if(r!=col){double f=m[r,col]/m[col,col];for(int k=col;k<4;k++)m[r,k]-=f*m[col,k];}
                }
                if(!ok)break;for(int r=0;r<3;r++)c[r]=m[r,3]/m[r,r];
                for(int i=0;i<n;i++){double x=(double)i/(n-1),res=sm[i]-(c[0]+c[1]*x+c[2]*x*x);keep[i]=res<1000&&res>-1500;}
            }
            var floor=new double[n];for(int i=0;i<n;i++){double x=(double)i/(n-1);floor[i]=c[0]+c[1]*x+c[2]*x*x;}
            double common=floor.OrderBy(x=>x).ElementAt(n/10);
            uint[] rates={25,33,50,66,125,250,333,500,1000,1500,2000};
            for(int i=0;i<n;)
            {
                if(sm[i]<floor[i]+2000){i++;continue;}
                int begin=i,peak=i;
                while(i<n&&sm[i]>=floor[i]+2000){if(sm[i]>sm[peak])peak=i;i++;}
                if(begin==0||i==n||i-begin<3)continue;
                double half=floor[peak]+(sm[peak]-floor[peak])*.5;int l=peak,r=peak;
                while(l>begin&&sm[l-1]>half)l--;while(r+1<i&&sm[r+1]>half)r++;
                double frequency=10490.5+(l+r)*.5*step,measured=(r-l+1)*step*1000;
                if(measured<18||measured>2800)continue;
                uint rate=rates.OrderBy(v=>Math.Abs(Math.Log(v/measured))).First();
                if(Math.Abs(frequency-10491.5)<.3){frequency=10491.5;rate=1500;}
                found.Add(new ScanCarrier {FrequencyMhz=Math.Round(frequency,3),SymbolRateKsps=rate,Strength=sm[peak]-common});
            }
            var beacon=found.FirstOrDefault(x=>x.FrequencyMhz==10491.5&&x.SymbolRateKsps==1500);
            if(beacon==null)return new List<ScanCarrier>();
            beaconVisible=true;
            return found.Where(x=>x.FrequencyMhz>=10492&&x.FrequencyMhz<=10499.4&&x.Strength>=beacon.Strength*.6).OrderBy(x=>x.FrequencyMhz).ToList();
        }
    }
}
