import struct, sys, io

ALPHA = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._"

def decode(name):
    out = []
    for ch in name:
        o = ord(ch)
        if o == 0x4840: out.append('!')
        elif 0x4800 <= o < 0x4840: out.append(ALPHA[o - 0x4800])
        elif 0x3800 <= o < 0x4800:
            v = o - 0x3800
            out.append(ALPHA[v & 0x3f]); out.append(ALPHA[(v >> 6) & 0x3f])
        else: out.append(chr(o))
    return ''.join(out)

def read_cfb(path):
    data = open(path, 'rb').read()
    SS = 1 << struct.unpack_from('<H', data, 0x1e)[0]
    MS = 1 << struct.unpack_from('<H', data, 0x20)[0]
    nfat, dirstart, _, cutoff, ministart, nmini, difstart, ndif = struct.unpack_from('<IIIIIIII', data, 0x2c)
    off = lambda s: (s + 1) * SS
    difat = list(struct.unpack_from('<109I', data, 0x4c))[:nfat]
    d = difstart
    per = SS // 4 - 1
    for _ in range(ndif):
        if d >= 0xFFFFFFFA: break
        difat += list(struct.unpack_from('<%dI' % per, data, off(d)))
        d = struct.unpack_from('<I', data, off(d) + per * 4)[0]
    difat = [s for s in difat if s < 0xFFFFFFFA][:nfat]
    fat = []
    for fs in difat:
        fat += list(struct.unpack_from('<%dI' % (SS // 4), data, off(fs)))
    def chain(start, size=None):
        b = b''; s = start; seen = set()
        while s < 0xFFFFFFFA and s not in seen:
            seen.add(s); b += data[off(s):off(s) + SS]
            s = fat[s] if s < len(fat) else 0xFFFFFFFE
        return b[:size] if size is not None else b
    dirdata = chain(dirstart)
    mini = b''
    for i in range(len(dirdata) // 128):
        e = dirdata[i*128:(i+1)*128]
        if e[66] == 5:
            st, sz = struct.unpack_from('<IQ', e, 116); mini = chain(st, sz); break
    minifat_b = chain(ministart)
    minifat = list(struct.unpack_from('<%dI' % (len(minifat_b)//4), minifat_b, 0))
    def minichain(start, size):
        b = b''; s = start; seen = set()
        while s < 0xFFFFFFFA and s not in seen:
            seen.add(s); b += mini[s*MS:(s+1)*MS]
            s = minifat[s] if s < len(minifat) else 0xFFFFFFFE
        return b[:size]
    out = {}
    for i in range(len(dirdata) // 128):
        e = dirdata[i*128:(i+1)*128]
        if e[66] != 2: continue
        nl = struct.unpack_from('<H', e, 64)[0]
        nm = e[:nl-2].decode('utf-16-le')
        st, sz = struct.unpack_from('<IQ', e, 116)
        out[decode(nm)] = minichain(st, sz) if sz < cutoff else chain(st, sz)
    return out

streams = read_cfb(sys.argv[1])
pool = streams['!_StringPool']
sdata = streams['!_StringData']

n = len(pool) // 4
p = struct.unpack_from('<%dH' % (n * 2), pool, 0)
codepage = p[0] | ((p[1] & ~0x8000) << 16)
long_refs = bool(p[1] & 0x8000)
print(f"codepage={codepage} longStringRefs={long_refs} poolEntries={n} dataLen={len(sdata)}")

enc = 'utf-8' if codepage == 65001 else 'cp1252'
strings = {0: ''}
off = 0; i = 1
while i < n:
    ln, refs = p[i*2], p[i*2+1]
    if ln == 0 and refs != 0:
        ln = (refs << 16) | p[i*2+2]
        i += 1
    strings[i] = sdata[off:off+ln].decode(enc, 'replace')
    off += ln
    i += 1
print(f"parsed {len(strings)-1} strings, consumed {off}/{len(sdata)} bytes  -> {'EXACT' if off==len(sdata) else 'MISMATCH'}")
print("first 18:", [strings[k] for k in list(strings)[1:19]])

sref = 3 if long_refs else 2
def rd_strref(b, o):
    return int.from_bytes(b[o:o+sref], 'little')

def signed32(v):
    """MSI stores int32 cells biased; bring the result back into signed range."""
    return v - (1 << 32) if v >= (1 << 31) else v


def parse(name, coldefs):
    """coldefs: list of (name, type) ; MSI tables are COLUMN-major."""
    b = streams['!' + name]
    widths = []
    for _, t in coldefs:
        if t & 0x0800: widths.append(sref)
        else: widths.append(2 if (t & 0xff) == 2 else 4)
    rowlen = sum(widths)
    rows = len(b) // rowlen if rowlen else 0
    cols = []
    o = 0
    for (cn, t), w in zip(coldefs, widths):
        vals = []
        for r in range(rows):
            raw = b[o + r*w: o + r*w + w]
            if t & 0x0800: vals.append(strings.get(int.from_bytes(raw, 'little'), '?'))
            elif w == 2:
                v = int.from_bytes(raw, 'little'); vals.append(None if v == 0 else v - 0x8000)
            else:
                v = int.from_bytes(raw, 'little'); vals.append(None if v == 0 else signed32(v ^ 0x80000000))
        cols.append(vals); o += rows * w
    return [tuple(c[r] for c in cols) for r in range(rows)]

STR = 0x0D00 | 72
I2 = 0x0102
tables = [r[0] for r in parse('_Tables', [('Name', STR)])]
print("\ntables:", tables)
cols = parse('_Columns', [('Table', STR), ('Number', I2), ('Name', STR), ('Type', I2)])
schema = {}
for t, num, nm, ty in cols:
    schema.setdefault(t, []).append((num, nm, ty & 0xFFFF if ty >= 0 else ty + 0x10000))
for t in schema: schema[t].sort()

print("\n--- Property ---")
for r in parse('Property', [(n_, ty) for _, n_, ty in schema['Property']]):
    print("   ", r)
print("\n--- Upgrade schema ---", schema['Upgrade'])
for r in parse('Upgrade', [(n_, ty) for _, n_, ty in schema['Upgrade']]):
    print("   ", r)
print("\n--- InstallExecuteSequence (RemoveExistingProducts etc) ---")
seq = parse('InstallExecuteSequence', [(n_, ty) for _, n_, ty in schema['InstallExecuteSequence']])
for r in sorted(seq, key=lambda x: x[2] or 0):
    print(f"    {r[2]:>5}  {r[0]}")
