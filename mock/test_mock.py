"""Teste do mock no PC: levanta o servidor, bate nos endpoints, valida e desliga."""
import json
import subprocess
import time
import urllib.request

BASE = "http://127.0.0.1:8080/mock"
CHECKS = [
    ("api/v2.10/busstops", list, 12),
    ("api/v2.10/routes", list, 10),
    ("api/v2.10/routes/726", dict, None),
    ("api/v2.10/Routes/busStop/1001", list, None),
    ("api/v2.10/estimations/estimationbusstop/1001/top/10", list, 3),
    ("api/v2.10/stoptimes/routeNumber/726/direction/Ida/stop/1001/date/2026-10-14", list, 8),
    ("api/v2.10/vehicleStatuses/routeNumber/726", list, 2),
    ("api/v2.10/planner/startLat/38.74/startLon/-9.14/endLat/38.70/endLon/-9.14/start/now/language/pt", list, 1),
    ("api/v2.10/geocoding/suggest/campo", list, 3),
    ("api/v2.10/geocoding/reverse/lat/38.74/lng/-9.14", list, 3),
    ("api/v2.10/alerts", list, 2),
]


def get(path, data=None, method="GET"):
    req = urllib.request.Request(f"{BASE}/{path}", data=data, method=method)
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.load(r)


srv = subprocess.Popen(["python3", "server.py"], cwd="/home/diogoaguiar/carris-app/rebuild/mock",
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
try:
    time.sleep(1)
    for path, kind, n in CHECKS:
        obj = get(path)
        assert isinstance(obj, kind), f"{path}: esperava {kind}, veio {type(obj)}"
        if n is not None:
            assert len(obj) == n, f"{path}: esperava {n}, veio {len(obj)}"
        print(f"OK  GET {path} ({len(obj) if isinstance(obj, list) else 'obj'})")
    tok = get("api/v2.11/register", data=b"{}", method="POST")
    assert tok.get("token") == "mock-jwt-token", tok
    print("OK  POST api/v2.11/register (token)")
    e1 = get("api/v2.10/estimations/estimationbusstop/1001/top/10")
    time.sleep(11)
    e2 = get("api/v2.10/estimations/estimationbusstop/1001/top/10")
    s1 = e1[0]["minutes"] * 60 + e1[0]["seconds"]
    s2 = e2[0]["minutes"] * 60 + e2[0]["seconds"]
    assert s2 < s1, f"contagem devia descer: {s1} -> {s2}"
    print(f"OK  countdown 10s ({s1}s -> {s2}s)")
    print("TUDO VERDE")
finally:
    srv.terminate()
