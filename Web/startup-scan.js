// Port of SDR-Tiouner app/auto_monitor.h's carrier detection and startup selection.
// Uses a fitted band floor and the beacon as a strength reference.
function pickStartupStations(fft, count) {
  if (!fft || fft.length < 32 || count <= 0) return [];
  const n=fft.length, step=9/(n-1);
  const smooth=fft.map((_,i)=>{
    let sum=0,k=0;
    for(let j=Math.max(0,i-3);j<=Math.min(n-1,i+3);j++){sum+=fft[j];k++;}
    return sum/k;
  });
  const sorted=[...smooth].sort((a,b)=>a-b);
  let c=[sorted[Math.floor(n/2)],0,0], keep=smooth.map(v=>v<=c[0]);
  for(let pass=0;pass<5;pass++){
    const m=Array.from({length:3},()=>[0,0,0,0]);let used=0;
    for(let i=0;i<n;i++)if(keep[i]){
      used++;const x=i/(n-1),p=[1,x,x*x];
      for(let r=0;r<3;r++){for(let k=0;k<3;k++)m[r][k]+=p[r]*p[k];m[r][3]+=p[r]*smooth[i];}
    }
    if(used<16)break;
    let ok=true;
    for(let col=0;col<3;col++){
      let pivot=col;
      for(let r=col+1;r<3;r++)if(Math.abs(m[r][col])>Math.abs(m[pivot][col]))pivot=r;
      if(Math.abs(m[pivot][col])<1e-12){ok=false;break;}
      [m[col],m[pivot]]=[m[pivot],m[col]];
      for(let r=0;r<3;r++)if(r!==col){const f=m[r][col]/m[col][col];for(let k=col;k<4;k++)m[r][k]-=f*m[col][k];}
    }
    if(!ok)break;
    c=m.map((row,r)=>row[3]/row[r]);
    keep=smooth.map((v,i)=>{const x=i/(n-1),residual=v-(c[0]+c[1]*x+c[2]*x*x);return residual<1000&&residual>-1500;});
  }
  const floor=smooth.map((_,i)=>{const x=i/(n-1);return c[0]+c[1]*x+c[2]*x*x;});
  const common=[...floor].sort((a,b)=>a-b)[Math.floor(n/10)];
  const rates=[25,33,50,66,125,250,333,500,1000,1500,2000],found=[];
  for(let i=0;i<n;){
    if(smooth[i]<floor[i]+2000){i++;continue;}
    const begin=i;let peak=i;
    while(i<n&&smooth[i]>=floor[i]+2000){if(smooth[i]>smooth[peak])peak=i;i++;}
    if(begin===0||i===n||i-begin<3)continue;
    const half=floor[peak]+(smooth[peak]-floor[peak])*.5;
    let left=peak,right=peak;
    while(left>begin&&smooth[left-1]>half)left--;
    while(right+1<i&&smooth[right+1]>half)right++;
    let frequencyMhz=10490.5+(left+right)*.5*step;
    const measured=(right-left+1)*step*1000;
    if(measured<18||measured>2800)continue;
    let symbolRateKsps=rates.reduce((best,v)=>Math.abs(Math.log(v/measured))<Math.abs(Math.log(best/measured))?v:best,rates[0]);
    if(Math.abs(frequencyMhz-10491.5)<.3){frequencyMhz=10491.5;symbolRateKsps=1500;}
    found.push({frequencyMhz:Math.round(frequencyMhz*1000)/1000,symbolRateKsps,strength:smooth[peak]-common});
  }
  const beacon=found.find(c=>c.frequencyMhz===10491.5&&c.symbolRateKsps===1500);
  if(!beacon)return [];
  return found.filter(c=>c.frequencyMhz>=10492&&c.frequencyMhz<=10499.4&&c.strength>=.6*beacon.strength)
    .sort((a,b)=>a.frequencyMhz-b.frequencyMhz).slice(0,count);
}
