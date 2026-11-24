import numpy as np
exec(open(__file__.replace('q2.py','q.py')).read().split("GLx=")[0])
GLx=np.array([-np.sqrt(.6),0,np.sqrt(.6)]); GLw=np.array([5,8,5])/9
rng=np.random.default_rng(1)
def phiGL3(p,dirv,a,b,tc,Hp):
    pa=np.arctan((a-tc)/Hp); pb=np.arctan((b-tc)/Hp)
    ph=(pa+pb)/2+(pb-pa)/2*GLx; tt=tc+Hp*np.tan(ph)
    return (pb-pa)/2*np.sum(GLw*g(p,dirv,tt))/Hp
def halfGL3(p,dirv,a,b,tc,Hp,dt=np.float64,quarter=False):
    # trig-free: nodes at u in GL3, psi = 2 atan(q u), q = tan(h/2)
    A=dt(a-tc); B=dt(b-tc); H=dt(Hp)
    s2=H*(B-A); c2=H*H+A*B; N=np.sqrt((H*H+A*A)*(H*H+B*B))
    tanh_ = s2/(c2+N) if c2>=0 else (N-c2)/s2          # tan(h), h = half the angle span
    q = tanh_/(1+np.sqrt(1+tanh_*tanh_))                # tan(h/2) in [0,1]
    if quarter: q = q/(1+np.sqrt(1+q*q))                # tan(h/4)
    ra=1/np.sqrt(H*H+A*A); rb=1/np.sqrt(H*H+B*B)
    tm=(A*ra+B*rb)/(H*(ra+rb))                          # tan(phi_mid)
    tot=dt(0)
    for u,w in zip(GLx.astype(dt),GLw.astype(dt)):
        qu=q*u
        if quarter:
            t2=2*qu/(1-qu*qu); tp=2*t2/(1-t2*t2); jac=4*q/(1+qu*qu)
        else:
            tp=2*qu/(1-qu*qu); jac=2*q/(1+qu*qu)        # tan(psi), dpsi/du
        tn=(tm+tp)/(1-tm*tp)                            # tan(phi_mid+psi)
        s=H*tn
        tot+=w*jac*g(p,dirv,np.array([tc+float(s)]))[0]
    return float(tot)/Hp
errs={'phi-GL3 (current)':[], 'halfangle-GL3 (trig-free)':[], 'quarterangle-GL3':[], 'halfangle-GL3 float32':[]}
for it in range(3000):
    p=rng.uniform(-1.5,1.5,3); p[2]=rng.uniform(-0.5,1.5)
    tgt=rng.normal(size=3)*0.3; tgt[2]=abs(tgt[2])+0.3
    dirv=tgt-p; dirv/=np.linalg.norm(dirv)
    t=np.linspace(0,4,200001); ft=f(p,dirv,t); ref=np.trapezoid(ft,t)
    if ref<1e-4: continue
    P=p[None,:]+t[:,None]*dirv[None,:]; d=np.sqrt((P**2).sum(1)); c=P[:,2]/d
    state=np.where(d>=r,0,np.where(c<=cosO,0,np.where(c>=cosO+1/scale,2,1)))
    ch=np.nonzero(np.diff(state))[0]; bps=[t[0]]+[t[i] for i in ch]+[t[-1]]
    H=np.linalg.norm(np.cross(p,dirv)); tc=-np.dot(p,dirv); Hp=np.sqrt(H*H+eps*eps)
    segs=[(a,b) for a,b in zip(bps[:-1],bps[1:]) if state[np.searchsorted(t,(a+b)/2)]!=0]
    for k,fn in [('phi-GL3 (current)',lambda a,b:phiGL3(p,dirv,a,b,tc,Hp)),
                 ('halfangle-GL3 (trig-free)',lambda a,b:halfGL3(p,dirv,a,b,tc,Hp)),
                 ('quarterangle-GL3',lambda a,b:halfGL3(p,dirv,a,b,tc,Hp,quarter=True)),
                 ('halfangle-GL3 float32',lambda a,b:halfGL3(p,dirv,a,b,tc,Hp,dt=np.float32))]:
        tot=sum(fn(a,b) for a,b in segs); errs[k].append(abs(tot-ref)/ref)
for k,v in errs.items():
    v=np.array(v); print(f"{k:28s} n={len(v)} mean {v.mean()*100:.3f}% p99 {np.percentile(v,99)*100:.2f}% max {v.max()*100:.2f}%")
