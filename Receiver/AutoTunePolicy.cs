using System;
using System.Collections.Generic;
using System.Linq;

namespace QocastPlayer.Receiver
{
    internal sealed class TunerSlot
    {
        public int Number;
        public double? Frequency;
        public uint Rate;
        public bool Connected, Locked, HadLock;
        public DateTime AssignedAt;
        public DateTime? LostAt;
        public bool Parked { get { return !Frequency.HasValue; } }
        public bool OnBeacon { get { return Frequency.HasValue && Math.Abs(Frequency.Value-10491.5)<.001; } }
        public void Assign(double frequency, uint rate, DateTime now)
        { Frequency=frequency;Rate=rate;AssignedAt=now;HadLock=false;LostAt=null;Locked=false; }
        public void Park() { Frequency=null;Rate=0;HadLock=false;LostAt=null;Locked=false; }
    }
    internal sealed class TuneAction
    {
        public TunerSlot Slot;
        public ScanCarrier Carrier; // null = park
    }
    internal sealed class AutoTunePolicy
    {
        private sealed class Seen { public ScanCarrier Carrier; public int Count; }
        private readonly List<Seen> _seen=new List<Seen>();
        private readonly List<Tuple<double,DateTime>> _cooldown=new List<Tuple<double,DateTime>>();
        private DateTime? _freeSince;
        public static bool Same(double a,double b,uint rateA,uint rateB)
        { return Math.Abs(a-b)<Math.Max(.035,Math.Min(rateA,rateB)/1000.0*.4); }
        public void Cooldown(double frequency,DateTime now)
        { _cooldown.Add(Tuple.Create(frequency,now.AddSeconds(15))); }
        public List<TuneAction> Tick(IList<TunerSlot> slots,List<ScanCarrier> carriers,bool fresh,bool beaconVisible,bool newFrame,bool auto,DateTime now)
        {
            var actions=new List<TuneAction>();
            _cooldown.RemoveAll(x=>x.Item2<=now);
            foreach(var slot in slots.Where(s=>!s.Parked))
            {
                if(slot.Locked){slot.HadLock=true;slot.LostAt=null;continue;}
                if(slot.HadLock && !slot.LostAt.HasValue)slot.LostAt=now;
                bool timeout=slot.HadLock ? now-slot.LostAt.Value>=TimeSpan.FromSeconds(5) : now-slot.AssignedAt>=TimeSpan.FromSeconds(10);
                // A tuner on the beacon keeps searching for it; it is given up only for a station.
                if(timeout && !slot.OnBeacon)
                { Cooldown(slot.Frequency.Value,now);slot.Park();actions.Add(new TuneAction{Slot=slot}); }
            }
            if(!fresh){_seen.Clear();_freeSince=null;return actions;}
            if(newFrame)
            {
                var next=new List<Seen>();
                foreach(var c in carriers){var previous=_seen.FirstOrDefault(x=>Same(x.Carrier.FrequencyMhz,c.FrequencyMhz,x.Carrier.SymbolRateKsps,c.SymbolRateKsps));next.Add(new Seen{Carrier=c,Count=previous==null?1:previous.Count+1});}
                _seen.Clear();_seen.AddRange(next);
            }
            // Stations come first; a tuner on the beacon yields to a station when no tuner is parked.
            if(auto)
            {
                foreach(var candidate in _seen.Where(x=>x.Count>=3).Select(x=>x.Carrier))
                {
                    if(_cooldown.Any(x=>Math.Abs(x.Item1-candidate.FrequencyMhz)<.06))continue;
                    if(slots.Any(s=>!s.Parked&&Same(s.Frequency.Value,candidate.FrequencyMhz,s.Rate,candidate.SymbolRateKsps)))continue;
                    var slot=slots.Where(s=>s.Parked&&s.Connected).OrderBy(s=>s.Number).FirstOrDefault()
                        ?? slots.Where(s=>s.OnBeacon&&s.Connected).OrderBy(s=>s.Number).FirstOrDefault();
                    if(slot==null)break;
                    slot.Assign(candidate.FrequencyMhz,candidate.SymbolRateKsps,now);
                    actions.Add(new TuneAction{Slot=slot,Carrier=candidate});
                }
            }
            // The beacon is shown only on a tuner that is left over, whether or not AutoTune is on.
            var free=slots.Where(s=>s.Parked&&s.Connected).OrderBy(s=>s.Number).FirstOrDefault();
            if(beaconVisible&&free!=null&&!slots.Any(s=>s.OnBeacon))
            {
                if(!_freeSince.HasValue)_freeSince=now;
                if(now-_freeSince.Value>=TimeSpan.FromSeconds(5))
                { free.Assign(10491.5,1500,now);actions.Add(new TuneAction{Slot=free,Carrier=new ScanCarrier{FrequencyMhz=10491.5,SymbolRateKsps=1500}});_freeSince=null; }
            }
            else _freeSince=null;
            return actions;
        }
    }
}
