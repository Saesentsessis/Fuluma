"""Static cost of an fxc -dumpbin listing (DXBC asm).

Counts scalar operations per class, for the whole program and for its shortest and longest path through
if/else/endif (loops are assumed unrolled; FXC unrolls everything in this shader). Then two rough throughput models:

  desktop (NVIDIA Ampere SM partition, 32-wide warps): 32 FP32 lanes and 4 SFU lanes per clock, so a warp's ALU
      instruction issues in 1 cycle and an SFU instruction occupies the SFU pipe for 8. The pipes overlap:
      cycles per warp = max(ALU + SFU, 8 * SFU). Divide by 32 for partition-cycles per pixel.
  mobile (Mali Valhall-like arithmetic unit, 16-wide warps): FMA 1 cycle, SFU 4 cycles per op, no overlap:
      cycles per warp = ALU + 4 * SFU. Divide by 16 for cycles per pixel.

Counts are scalar ops: a vec3 mul is 3, dp3 is 3, div is rcp + mul, sincos is 1 ALU (range reduction) + 1 SFU per output.

Both are models from the instruction mix, not vendor-compiler output (malioc / RGA give real numbers, section 5 of
ShaderInspection.md). Use them to compare the two shaders, not as absolute costs.

Usage: python dxbc_cost.py <listing.asm> [<listing.asm> ...]
"""
import re
import sys

SFU = {'rsq', 'sqrt', 'rcp', 'exp', 'log', 'sincos'}
FLOW = {'if', 'else', 'endif', 'loop', 'endloop', 'break', 'breakc', 'continue', 'continuec', 'ret', 'retc',
        'discard', 'switch', 'case', 'default', 'endswitch'}
MEMORY_PREFIX = ('ld', 'sample', 'gather', 'store', 'atomic', 'imm_atomic', 'bufinfo', 'resinfo')
DOT = {'dp2': 2, 'dp3': 3, 'dp4': 4}

SWIZZLE = re.compile(r'^[-|]*[a-z]+\d*(\[[^\]]*\])?\.([xyzw]+)')


def components(operand):
    operand = operand.strip()
    if operand.startswith('null'):
        return 0
    m = SWIZZLE.match(operand)
    return len(m.group(2)) if m else 4


def split_operands(rest):
    out, depth, cur = [], 0, ''
    for ch in rest:
        if ch in '([':
            depth += 1
        elif ch in ')]':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur)
            cur = ''
        else:
            cur += ch
    if cur.strip():
        out.append(cur)
    return out


def cost(line):
    """(alu, sfu, mem, flow-token) of one instruction line."""
    text = line.strip()
    token, _, rest = text.partition(' ')
    op = token.split('(')[0]
    if op.endswith('_sat'):
        op = op[:-4]
    base = op.split('_')[0]

    if base in FLOW:
        return None, base
    if op.startswith(MEMORY_PREFIX):
        return (0, 0, 1), None

    operands = split_operands(rest)
    if base == 'sincos':
        n = components(operands[0]) + components(operands[1])
        return (n, n, 0), None  # range reduction (1 ALU) + 1 SFU per output
    dest = components(operands[0]) if operands else 1
    if base in DOT:
        return (DOT[base], 0, 0), None
    if base == 'div':
        return (dest, dest, 0), None  # rcp + mul
    if base in SFU:
        return (0, dest, 0), None
    return (dest, 0, 0), None


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def parse(lines):
    """Returns (static, shortest, longest) cost tuples (alu, sfu, mem)."""
    zero = (0, 0, 0)
    # Stack of frames: [static, short, long, kind, then-branch(short,long) or None]
    stack = [[zero, zero, zero]]
    branch = []  # per open if: dict with 'before' frame totals and 'then' results

    for line in lines:
        c, flow = cost(line)
        if flow is None:
            top = stack[-1]
            top[0], top[1], top[2] = add(top[0], c), add(top[1], c), add(top[2], c)
            continue
        if flow == 'if':
            top = stack[-1]
            top[0], top[1], top[2] = add(top[0], (0, 0, 0)), top[1], top[2]
            branch.append({'then': None})
            stack.append([zero, zero, zero])
        elif flow == 'else':
            frame = stack.pop()
            branch[-1]['then'] = frame
            stack.append([zero, zero, zero])
        elif flow == 'endif':
            frame = stack.pop()
            b = branch.pop()
            if b['then'] is None:  # if without else: the skipped path is empty
                then, other = frame, [zero, zero, zero]
            else:
                then, other = b['then'], frame
            top = stack[-1]
            top[0] = add(add(top[0], then[0]), other[0])
            top[1] = add(top[1], min(then[1], other[1], key=weight_mobile))
            top[2] = add(top[2], max(then[2], other[2], key=weight_mobile))
    return tuple(stack[0])


def weight_mobile(c):
    return c[0] + 4 * c[1]


def desktop(c):
    return max(c[0] + c[1], 8 * c[1])


def instructions(lines):
    n = 0
    for line in lines:
        t = line.strip()
        if not t or t.startswith('//') or t.startswith('dcl_') or re.match(r'^(vs|ps|cs)_\d', t):
            continue
        if re.match(r'^[a-z]', t):
            n += 1
    return n


def program_lines(path):
    out = []
    for line in open(path, encoding='utf-8', errors='replace'):
        t = line.strip()
        if not t or t.startswith('//') or t.startswith('dcl_') or re.match(r'^(vs|ps|cs)_\d', t):
            continue
        if re.match(r'^[a-z]', t):
            out.append(t)
    return out


def main():
    print(f"{'program':58} {'instr':>5} | {'path':8} {'ALU':>5} {'SFU':>4} {'mem':>3} | {'desktop':>7} {'mobile':>6}")
    for path in sys.argv[1:]:
        lines = program_lines(path)
        static, short, long_ = parse(lines)
        name = '/'.join(path.replace('\\', '/').split('/')[-2:]).replace('.asm', '')  # parent folder/file, if any
        for label, c in (('static', static), ('shortest', short), ('longest', long_)):
            print(f"{name if label == 'static' else '':58} {instructions(lines) if label == 'static' else '':>5} | "
                  f"{label:8} {c[0]:5} {c[1]:4} {c[2]:3} | {desktop(c):7} {weight_mobile(c):6}")


if __name__ == '__main__':
    main()
