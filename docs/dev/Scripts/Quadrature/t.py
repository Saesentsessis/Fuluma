import numpy as np
x=np.linspace(-1,1,200001)
fa=x*(-0.1784*abs(x)-0.0663*x*x+1.0301)
print("drobot atanFast4 max err rad", np.max(abs(fa-np.arctan(x))))
ra=np.pi/4*x - x*(abs(x)-1)*(0.2447+0.0663*abs(x))
print("rajan max err", np.max(abs(ra-np.arctan(x))))
# odd minimax deg 7 / 9 fit on [0,1]
for n in (3,4,5,6):
    xs=np.linspace(0,1,4001); A=np.stack([xs**(2*k+1) for k in range(n)],1)
    c=np.linalg.lstsq(A,np.arctan(xs),rcond=None)[0]
    # crude: lsq not minimax
    print("odd poly terms",n,"lsq max err",np.max(abs(A@c-np.arctan(xs))))
