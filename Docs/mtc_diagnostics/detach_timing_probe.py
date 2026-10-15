#!/usr/bin/env python3
"""Talks to the ROS-TCP-Endpoint like Unity does, triggers one plan+execute through the legacy
/pick_place_task topic (object placed back where it is) and logs the arrival times of
/joint_states motion, /attached_collision_objects_ros, /collision_objects_ros and
/monitored_planning_scene so the detach can be timed against the robot motion."""
import json
import socket
import struct
import sys
import time

HOST = sys.argv[1] if len(sys.argv) > 1 else "10.201.196.129"
PORT = 10000
RECORD_SECONDS = float(sys.argv[2]) if len(sys.argv) > 2 else 150.0
PLACE_Z_OFFSET = -0.1251  # cancels the server's 0.5*object_dimensions[0] + place_surface_offset
CDR_HEADER = b"\x00\x01\x00\x00"


# ----------------------------------------------------------------------------- CDR
class Reader:
    def __init__(self, payload: bytes):
        self.b = payload[4:]  # strip the encapsulation header; alignment is relative to here
        self.o = 0

    def align(self, n):
        pad = (-self.o) % n
        self.o += pad

    def u8(self):
        v = self.b[self.o]; self.o += 1; return v

    def i32(self):
        self.align(4); v = struct.unpack_from("<i", self.b, self.o)[0]; self.o += 4; return v

    def u32(self):
        self.align(4); v = struct.unpack_from("<I", self.b, self.o)[0]; self.o += 4; return v

    def f32(self):
        self.align(4); v = struct.unpack_from("<f", self.b, self.o)[0]; self.o += 4; return v

    def f64(self):
        self.align(8); v = struct.unpack_from("<d", self.b, self.o)[0]; self.o += 8; return v

    def string(self):
        n = self.u32()
        s = self.b[self.o:self.o + n - 1].decode("utf-8", "replace") if n > 0 else ""
        self.o += n
        return s

    def f64s(self):
        n = self.u32(); return [self.f64() for _ in range(n)]

    def strings(self):
        n = self.u32(); return [self.string() for _ in range(n)]

    def header(self):
        sec, nsec = self.i32(), self.u32(); fid = self.string(); return (sec, nsec, fid)

    def pose(self):
        return [self.f64() for _ in range(7)]  # x y z qx qy qz qw

    def poses(self):
        n = self.u32(); return [self.pose() for _ in range(n)]

    def solid_primitive(self):
        t = self.u8(); dims = self.f64s()
        npts = self.u32()  # polygon.points (Point32[])
        for _ in range(npts):
            self.f32(); self.f32(); self.f32()
        return (t, dims)

    def mesh(self):
        ntri = self.u32()
        for _ in range(ntri):
            self.u32(); self.u32(); self.u32()
        nv = self.u32()
        for _ in range(nv):
            self.f64(); self.f64(); self.f64()
        return (ntri, nv)

    def collision_object(self):
        co = {}
        co["header"] = self.header()
        co["pose"] = self.pose()
        co["id"] = self.string()
        self.string(); self.string()  # type.key, type.db
        n = self.u32(); prims = [self.solid_primitive() for _ in range(n)]
        self.poses()
        n = self.u32(); meshes = [self.mesh() for _ in range(n)]
        self.poses()
        n = self.u32()
        for _ in range(n):
            [self.f64() for _ in range(4)]
        self.poses()
        self.strings(); self.poses()
        co["operation"] = self.u8()
        co["geom"] = f"{len(prims)} prim, {sum(m[0] for m in meshes)} tris"
        return co

    def attached_collision_object(self):
        link = self.string()
        co = self.collision_object()
        co["link"] = link
        return co

    def joint_state(self):
        self.header()
        names = self.strings()
        pos = self.f64s()
        return names, pos


# ----------------------------------------------------------------------------- framing
class Endpoint:
    def __init__(self, host, port):
        self.sock = socket.create_connection((host, port), timeout=10)
        self.sock.settimeout(0.5)
        self.buf = b""

    def send_frame(self, topic: str, payload: bytes):
        t = topic.encode()
        self.sock.sendall(struct.pack("<I", len(t)) + t + struct.pack("<I", len(payload)) + payload)

    def send_sys(self, command: str, params: dict):
        j = json.dumps(params).encode()
        self.send_frame(command, struct.pack("<I", len(j) + 1) + j + b"\x00")

    def _read_exact(self, n):
        while len(self.buf) < n:
            try:
                chunk = self.sock.recv(1 << 20)
            except socket.timeout:
                return None
            if not chunk:
                raise ConnectionError("endpoint closed the connection")
            self.buf += chunk
        out, self.buf = self.buf[:n], self.buf[n:]
        return out

    def read_frame(self):
        """Returns (topic, payload) or None on timeout. Partial reads are kept in the buffer."""
        saved = self.buf
        head = self._read_exact(4)
        if head is None:
            return None
        (tl,) = struct.unpack("<I", head)
        topic = self._read_exact(tl)
        if topic is None:
            self.buf = saved; return None
        head2 = self._read_exact(4)
        if head2 is None:
            self.buf = saved; return None
        (pl,) = struct.unpack("<I", head2)
        payload = self._read_exact(pl)
        if payload is None:
            self.buf = saved; return None
        return topic.decode("ascii", "replace"), payload


def cdr_string(s: str, w: "Writer"):
    w.align(4); b = s.encode(); w.buf += struct.pack("<I", len(b) + 1) + b + b"\x00"


class Writer:
    def __init__(self):
        self.buf = b""

    def align(self, n):
        self.buf += b"\x00" * ((-len(self.buf)) % n)

    def i32(self, v): self.align(4); self.buf += struct.pack("<i", v)
    def u32(self, v): self.align(4); self.buf += struct.pack("<I", v)
    def f64(self, v): self.align(8); self.buf += struct.pack("<d", v)
    def string(self, s): cdr_string(s, self)

    def payload(self):
        return CDR_HEADER + self.buf + b"\x00\x00\x00\x00"  # trailing pad like the connector


def pick_place_task_msg(object_id, frame_id, pose):
    w = Writer()
    w.string(object_id)
    w.i32(0); w.u32(0); w.string(frame_id)  # header
    for v in pose:
        w.f64(v)
    return w.payload()


def get_planning_scene_request(components):
    w = Writer(); w.u32(components); return w.payload()


# ----------------------------------------------------------------------------- main
def main():
    t0 = time.time()
    el = lambda: time.time() - t0
    ep = Endpoint(HOST, PORT)
    topic, payload = None, None
    while topic is None:
        fr = ep.read_frame()
        if fr: topic, payload = fr
    print(f"[{el():7.3f}] handshake: {topic} {payload[4:].rstrip(b'\x00')[:120]!r}")

    subs = {
        "/attached_collision_objects_ros": "moveit_msgs/AttachedCollisionObject",
        "/collision_objects_ros": "moveit_msgs/CollisionObject",
        "/joint_states": "sensor_msgs/JointState",
        "/monitored_planning_scene": "moveit_msgs/PlanningScene",
    }
    for t, m in subs.items():
        ep.send_sys("__subscribe", {"topic": t, "message_name": m})

    # --- current scene: where are the blanks?
    ep.send_sys("__ros_service", {"topic": "/get_planning_scene", "message_name": "moveit_msgs/GetPlanningScene"})
    ep.send_sys("__request", {"srv_id": 1})
    ep.send_frame("/get_planning_scene", get_planning_scene_request(4 | 8 | 16))
    scene = None
    expecting_response = False
    deadline = time.time() + 15
    while scene is None and time.time() < deadline:
        fr = ep.read_frame()
        if not fr: continue
        t, p = fr
        if t == "__response":
            expecting_response = True
        elif expecting_response and t == "/get_planning_scene":
            scene = p
        elif t.startswith("__"):
            print(f"[{el():7.3f}] sys {t}: {p[4:].rstrip(b'\x00')[:200]!r}")
    if scene is None:
        print("no planning scene response"); return
    print(f"[{el():7.3f}] planning scene response: {len(scene)} bytes")

    blanks = {}
    start = 0
    while True:
        i = scene.find(b"blank_", start)
        if i < 0: break
        start = i + 1
        end = scene.find(b"\x00", i)
        name = scene[i:end].decode()
        (n,) = struct.unpack_from("<I", scene, i - 4)
        if n != len(name) + 1: continue
        pose = list(struct.unpack_from("<7d", scene, i - 4 - 56))
        blanks[name] = pose
    for k, v in sorted(blanks.items()):
        print(f"   {k}: pos=({v[0]:.4f}, {v[1]:.4f}, {v[2]:.4f}) q=({v[3]:.3f},{v[4]:.3f},{v[5]:.3f},{v[6]:.3f})")
    if not blanks:
        print("no blank_* objects in the scene"); return
    target_id = max(blanks, key=lambda k: blanks[k][2])
    pose = list(blanks[target_id])
    place = pose[:]
    place[2] += PLACE_Z_OFFSET
    print(f"[{el():7.3f}] picking {target_id} and placing it back (requested z {place[2]:.4f})")

    ep.send_sys("__publish", {"topic": "/pick_place_task", "message_name": "moveit_task_constructor_msgs/PickPlaceTask",
                              "queue_size": 10, "latch": False})
    time.sleep(0.5)
    ep.send_frame("/pick_place_task", pick_place_task_msg(target_id, "world", place))
    t_goal = el()
    print(f"[{t_goal:7.3f}] sent /pick_place_task for {target_id}")

    # --- record
    events = []           # (t, text)
    last_pos = None
    motion_segments = []  # [start, end]
    moving_until = None
    last_js_t = None
    js_count = 0
    deadline = time.time() + RECORD_SECONDS
    saw_detach = False
    saw_readd = False
    while time.time() < deadline:
        fr = ep.read_frame()
        if not fr: continue
        t, p = fr
        now = el()
        if t == "/joint_states":
            js_count += 1
            try:
                names, pos = Reader(p).joint_state()
            except Exception as e:  # noqa
                continue
            arm = [v for n, v in zip(names, pos) if "finger" not in n]
            fingers = [v for n, v in zip(names, pos) if "finger" in n]
            if last_pos is not None and len(last_pos) == len(pos):
                delta = max(abs(a - b) for a, b in zip(pos, last_pos))
                if delta > 1e-5:
                    if moving_until is None or now - moving_until > 0.6:
                        motion_segments.append([now, now])
                    motion_segments[-1][1] = now
                    moving_until = now
            last_pos = pos
            last_js_t = now
            if js_count % 100 == 0:
                events.append((now, f"joint_states #{js_count} arm={['%.3f' % v for v in arm]} fingers={['%.4f' % v for v in fingers]}"))
        elif t == "/attached_collision_objects_ros":
            co = Reader(p).attached_collision_object()
            op = "REMOVE" if co["operation"] == 1 else f"op{co['operation']}"
            events.append((now, f"ACO {op} '{co['id']}' link='{co['link']}' pose_in_link=({co['pose'][0]:.3f},{co['pose'][1]:.3f},{co['pose'][2]:.3f})"))
            print(f"[{now:7.3f}] {events[-1][1]}")
            if op == "REMOVE": saw_detach = True
        elif t == "/collision_objects_ros":
            co = Reader(p).collision_object()
            op = {0: "ADD", 1: "REMOVE", 2: "APPEND", 3: "MOVE"}.get(co["operation"], str(co["operation"]))
            events.append((now, f"CO {op} '{co['id']}' pos=({co['pose'][0]:.4f},{co['pose'][1]:.4f},{co['pose'][2]:.4f}) {co['geom']} {len(p)}B"))
            print(f"[{now:7.3f}] {events[-1][1]}")
            if co["id"] == target_id and op == "ADD" and saw_detach: saw_readd = True
        elif t == "/monitored_planning_scene":
            events.append((now, f"PlanningScene {len(p)} bytes"))
            print(f"[{now:7.3f}] {events[-1][1]}")
        elif t.startswith("__"):
            events.append((now, f"sys {t}: {p[4:].rstrip(b'\x00')[:300]!r}"))
            print(f"[{now:7.3f}] {events[-1][1]}")
        if saw_readd and moving_until is not None and now - moving_until > 4.0:
            break

    print("\n=== timeline ===")
    print(f"[{t_goal:7.3f}] goal sent")
    for s, e in motion_segments:
        print(f"[{s:7.3f}] -> [{e:7.3f}] robot moving ({e - s:.2f}s)")
    for tt, text in events:
        if text.startswith("joint_states"): continue
        print(f"[{tt:7.3f}] {text}")
    print(f"\njoint_states received: {js_count}; motion segments: {len(motion_segments)}; detach seen: {saw_detach}; re-add seen: {saw_readd}")


if __name__ == "__main__":
    main()
