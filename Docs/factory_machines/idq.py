# Query object names from an ID render. usage: idq.py <machine> <view> x0 y0 x1 y1 [x0 y0 x1 y1 ...]
# coordinates are image pixels (origin top-left, 1600x1200). Prints objects in each rect with pixel counts.
import sys, json, struct, numpy as np, os
SP = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
m, view = sys.argv[1], sys.argv[2]
bmp = f'{SP}/prep/{m}_{view}_id.bmp' if not view.endswith('.bmp') else (view if os.path.isabs(view) else f'{SP}/prep/{view}')
idmap = json.load(open(f'{SP}/prep/{m}_idmap.json'))
objs = {o['name']: o for o in json.load(open(f'{SP}/prep/{m}_objects.json'))['objects']}
data = open(bmp, 'rb').read()
off = struct.unpack_from('<I', data, 10)[0]; w = struct.unpack_from('<i', data, 18)[0]; h = struct.unpack_from('<i', data, 22)[0]; bpp = struct.unpack_from('<H', data, 28)[0]
row = ((w * bpp // 8 + 3) // 4) * 4
img = np.frombuffer(data, dtype=np.uint8, count=row * abs(h), offset=off).reshape(abs(h), row)[:, :w * (bpp // 8)].reshape(abs(h), w, bpp // 8)
if h > 0: img = img[::-1]
rgb = img[:, :, [2, 1, 0]]
nums = list(map(int, sys.argv[3:]))
idcols = np.array([list(map(int, k.split(','))) for k in idmap]); idnames = list(idmap.values())
def fmt(o):
    s = objs[o]; return f"{o:14s} faces={s['faces']:6d} size=({s['size'][0]:.2f},{s['size'][1]:.2f},{s['size'][2]:.2f}) ctr=({s['center'][0]:.2f},{s['center'][1]:.2f},{s['center'][2]:.2f}) mats={[x for x in s['mats'] if x][:2]}"
for i in range(0, len(nums), 4):
    x0, y0, x1, y1 = nums[i:i+4]
    sub = rgb[y0:y1, x0:x1].reshape(-1, 3)
    keys, counts = np.unique(sub, axis=0, return_counts=True)
    res = []
    agg = {}
    for k, c in zip(keys, counts):
        if int(k[0]) + int(k[1]) + int(k[2]) < 30: continue
        d = np.abs(idcols - k.astype(int)).sum(axis=1); j = int(d.argmin())
        if d[j] <= 6: agg[idnames[j]] = agg.get(idnames[j], 0) + int(c)
    res = sorted(((c, n) for n, c in agg.items()), reverse=True)
    print(f"--- rect ({x0},{y0})-({x1},{y1}): {len(res)} objects")
    for c, n in res[:25]: print(f"  {c:6d}px  {fmt(n)}")
