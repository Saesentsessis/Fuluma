import numpy as np
rng=np.random.default_rng(1)
r=1.0; eps=0.02*r; cosO=np.cos(np.radians(30)); scale=1/(np.cos(np.radians(20))-cosO)
def g(p,dirv,t):  # integrand without 1/(d^2+eps^2)
    P=p[None,:]+t[:,None]*dirv[None,:]
    d2=(P**2).sum(1); d=np.sqrt(d2)
    w=np.clip(1-d2/r**2,0,None)**2
    ang=np.clip((P[:,2]/np.maximum(d,1e-30)-cosO)*scale,0,1)**2
    return w*ang
def f(p,dirv,t):
    P=p[None,:]+t[:,None]*dirv[None,:]; d2=(P**2).sum(1)
    return g(p,dirv,t)/(d2+eps**2)
GLx=np.array([-np.sqrt(.6),0,np.sqrt(.6)]); GLw=np.array([5,8,5])/9
rules={'GL3':(GLx,GLw),'Simpson3(ends+mid)':(np.array([-1,0,1.]),np.array([1,4,1])/3),
 'Milne3(1/4,1/2,3/4)':(np.array([-.5,0,.5]),np.array([4,-2,4])/3),
 'Boole5':(np.array([-1,-.5,0,.5,1]),np.array([7,32,12,32,7])/45),
 'GL4':tuple(np.polynomial.legendre.leggauss(4)),'GL2':tuple(np.polynomial.legendre.leggauss(2))}
errs={k:[] for k in rules}
for it in range(3000):
    # random ray hitting the volume
    p=rng.uniform(-1.5,1.5,3); p[2]=rng.uniform(-0.5,1.5)
    tgt=rng.normal(size=3)*0.3; tgt[2]=abs(tgt[2])+0.3
    dirv=tgt-p; dirv/=np.linalg.norm(dirv)
    t=np.linspace(0,4,200001); ft=f(p,dirv,t)
    ref=np.trapezoid(ft,t)
    if ref<1e-4: continue
    # segment breakpoints: where angular hits 0 or 1, or window hits 0 (detect from samples)
    gt=g(p,dirv,t)
    P=p[None,:]+t[:,None]*dirv[None,:]; d=np.sqrt((P**2).sum(1)); c=P[:,2]/d
    state=np.where(d>=r,0,np.where(c<=cosO,0,np.where(c>=cosO+1/scale,2,1)))
    ch=np.nonzero(np.diff(state))[0]
    bps=[t[0]]+[t[i] for i in ch]+[t[-1]]
    H=np.linalg.norm(np.cross(p,dirv)); tc=-np.dot(p,dirv); Hp=np.sqrt(H*H+eps*eps)
    for k,(x,w) in rules.items():
        tot=0
        for a,b in zip(bps[:-1],bps[1:]):
            if state[np.searchsorted(t,(a+b)/2)]==0: continue
            pa=np.arctan((a-tc)/Hp); pb=np.arctan((b-tc)/Hp)
            ph=(pa+pb)/2+(pb-pa)/2*x
            tt=tc+Hp*np.tan(ph)
            tot+=(pb-pa)/2*np.sum(w*g(p,dirv,tt))/Hp
        errs[k].append(abs(tot-ref)/ref)
for k,v in errs.items():
    v=np.array(v); print(f"{k:22s} n={len(v)} mean {v.mean()*100:.3f}% p99 {np.percentile(v,99)*100:.2f}% max {v.max()*100:.2f}%")
