import numpy as np
GLx=np.array([-np.sqrt(.6),0,np.sqrt(.6)])
def nodes(A,B,H,dt):
    A=dt(A);B=dt(B);H=dt(H); one=dt(1)
    s2=H*(B-A); c2=H*H+A*B; N=np.sqrt((H*H+A*A)*(H*H+B*B))
    th=s2/(c2+N) if c2>=0 else (N-c2)/s2
    q=th/(one+np.sqrt(one+th*th))
    ra=one/np.sqrt(H*H+A*A); rb=one/np.sqrt(H*H+B*B); tm=(A*ra+B*rb)/(H*(ra+rb))
    out=[]
    for u in GLx.astype(dt):
        qu=q*u; tp=2*qu/(one-qu*qu); out.append(H*(tm+tp)/(one-tm*tp))
    return np.array(out,dtype=np.float64), 2*q/(1+(q*GLx)**2)
rng=np.random.default_rng(2); worst=0; worstj=0
for i in range(200000):
    H=10**rng.uniform(-4,1); A=rng.uniform(-3,3); B=A+10**rng.uniform(-4,0.8)
    n64,j64=nodes(A,B,H,np.float64); n32,j32=nodes(A,B,H,np.float32)
    e=np.max(abs(n32-n64))/max(B-A,1e-30); ej=np.max(abs(j32-j64)/j64)
    if e>worst: worst=e; wc=(A,B,H)
    if ej>worstj: worstj=ej; wj=(A,B,H)
print("worst node err / segment length",worst,wc); print("worst rel jacobian err",worstj,wj)
