"""Mock local da API Carris (contratos observados, dados fictícios)."""
import json
import re
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

FIX = Path(__file__).parent / "fixtures"
START = time.time()
POLL_SECONDS = 10  # cliente faz polling de 10s para não perder o autocarro


def load(name):
    return json.loads((FIX / name).read_text(encoding="utf-8"))


# Base por paragem (minutos à abertura do servidor); o mock anima a contagem.
EST_BASE = {
    "1001": [("726", "Pontinha", 4, "1234"), ("726", "Pontinha", 11, "1235"), ("727", "Restelo", 7, "1236")],
    "1002": [("727", "Restelo", 3, "1237"), ("736", "Odivelas", 9, "1238")],
    "1010": [("726", "Pontinha", 6, "1234"), ("744", "Moscavide", 12, "1240")],
}
EST_DEFAULT = [("726", "Pontinha", 8, "1234"), ("727", "Restelo", 14, "1236")]

# Viaturas por carreira: movem-se ao longo do segmento A->B em loop (período 120s).
VEH_BASE = {
    "726": [("1234", 38.7412, -9.1466, 38.7620, -9.1380), ("1235", 38.7620, -9.1380, 38.7412, -9.1466)],
    "727": [("1236", 38.7370, -9.1310, 38.7071, -9.1466)],
}
VEH_DEFAULT = [("1299", 38.7440, -9.1490, 38.7410, -9.1660)]
VEH_PERIOD = 120.0


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, obj, code=200):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, Authorization")
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, Authorization")
        self.end_headers()

    def do_GET(self):
        p = self.path
        if re.search(r"/busstops(/timestamp/\d+)?$", p):
            return self.send(load("busstops.json"))
        if "/routes/timestamp/" in p or re.search(r"/routes$", p):
            return self.send(load("routes.json"))
        if re.search(r"/routes/[^/]+$", p):
            n = p.rstrip("/").split("/")[-1]
            return self.send({"number": n, "variants": [{"id": "v1", "headsign": "Ida"}, {"id": "v2", "headsign": "Volta"}]})
        if "/Routes/busStop/" in p or "/routes/" in p and "/from/" in p:
            return self.send(load("routes.json"))
        if "/busstops/routenumber/" in p:
            return self.send(load("busstops.json"))
        if "/estimationbusstop/" in p:
            m = re.search(r"/estimationbusstop/([^/]+)/", p)
            stop = m.group(1) if m else ""
            elapsed = int(time.time() - START)
            out = []
            for route, head, base_min, veh in EST_BASE.get(stop, EST_DEFAULT):
                total = max(20, base_min * 60 - elapsed)  # contagem decrescente
                out.append({"routeNumber": route, "headsign": head, "minutes": total // 60,
                            "seconds": total % 60, "vehicleId": veh})
            return self.send(out)
        if "/stoptimes/" in p:
            return self.send(load("stoptimes.json"))
        if "/vehicleStatuses/" in p:
            m = re.search(r"/routeNumber/([^/]+)", p)
            route = m.group(1) if m else ""
            elapsed = time.time() - START
            out = []
            for veh, alat, alon, blat, blon in VEH_BASE.get(route, VEH_DEFAULT):
                k = ((elapsed % VEH_PERIOD) / VEH_PERIOD)  # 0->1 ida, volta a seguir
                k = k * 2 if k < 0.5 else 2 - k * 2
                out.append({"vehicleId": veh, "routeNumber": route,
                            "lat": round(alat + (blat - alat) * k, 6),
                            "lng": round(alon + (blon - alon) * k, 6)})
            return self.send(out)
        if "/planner/" in p:
            return self.send(load("planner.json"))
        if "/geocoding/" in p:
            return self.send(load("geocoding.json"))
        if p.rstrip("/").endswith("/alerts"):
            return self.send(load("alerts.json"))
        return self.send({"error": "unknown mock route", "path": p}, 404)

    def do_POST(self):
        if "/register" in self.path or "/authorization/sign" in self.path:
            length = int(self.headers.get("Content-Length", 0))
            self.rfile.read(length)
            return self.send({"token": "mock-jwt-token", "tokenType": "Bearer"})
        return self.send({"error": "unknown mock route"}, 404)


if __name__ == "__main__":
    srv = HTTPServer(("127.0.0.1", 8080), Handler)
    print("mock a correr em http://127.0.0.1:8080/mock (Ctrl+C para parar)")
    srv.serve_forever()
