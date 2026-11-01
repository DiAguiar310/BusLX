"""GTFS Carris: download, importação SQLite e consultas.

Dados públicos da Carris (plano Gravitee key_less), sem autenticação.
Feed estático: https://gateway.carris.pt/gateway/gtfs/api/v2.8/GTFS
Tempo real:    https://gateway.carris.pt/gateway/gtfs/api/v2.10/GTFS/realtime/vehiclepositions
"""
from __future__ import annotations

import csv
import io
import json
import math
import re
import sqlite3
import time
import unicodedata
import urllib.request
import zipfile
from datetime import date, datetime, time as dtime, timedelta
from pathlib import Path
from typing import Any, Iterable

GTFS_BASE = "https://gateway.carris.pt/gateway/gtfs/api"
GTFS_STATIC_URL = f"{GTFS_BASE}/v2.8/GTFS"
GTFS_RT_URL = f"{GTFS_BASE}/v2.10/GTFS/realtime/vehiclepositions"
USER_AGENT = "ViaLX/0.1 (projeto independente; GTFS público Carris)"

TIMEZONE_NAME = "Europe/Lisbon"
try:
    from zoneinfo import ZoneInfo

    TZ = ZoneInfo(TIMEZONE_NAME)
except Exception:  # pragma: no cover - fallback para sistemas sem tzdata
    TZ = None


def log(*args: Any) -> None:
    print(*args, flush=True)


def now_lisbon() -> datetime:
    return datetime.now(TZ) if TZ else datetime.now().astimezone()


def haversine_m(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    r = 6371000.0
    dlat = math.radians(lat2 - lat1)
    dlon = math.radians(lon2 - lon1)
    a = (
        math.sin(dlat / 2) ** 2
        + math.cos(math.radians(lat1))
        * math.cos(math.radians(lat2))
        * math.sin(dlon / 2) ** 2
    )
    return 2 * r * math.asin(math.sqrt(a))


def normalize(value: str) -> str:
    value = unicodedata.normalize("NFKD", value or "")
    value = value.encode("ascii", "ignore").decode("ascii").lower()
    return " ".join(value.split())


STOPWORDS = {"do", "da", "de", "dos", "das", "e", "a", "o", "os", "as", "em", "na", "no"}


def search_tokens(value: str) -> set[str]:
    return {t for t in normalize(value).replace("-", " ").split() if t not in STOPWORDS}


def parse_gtfs_time(value: str) -> int | None:
    if not value:
        return None
    try:
        h, m, s = value.split(":")
        return int(h) * 3600 + int(m) * 60 + int(s)
    except Exception:
        return None


def int_or(value: Any, default: int = 0) -> int:
    try:
        return int(float(value))
    except Exception:
        return default


def float_or(value: Any, default: float = 0.0) -> float:
    try:
        return float(value)
    except Exception:
        return default


def download_file(url: str, dest: Path, timeout: int = 300) -> Path:
    dest = Path(dest)
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(dest.suffix + ".part")
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    log(f"  a descarregar {url}")
    with urllib.request.urlopen(req, timeout=timeout) as response, tmp.open("wb") as handle:
        total = int(response.headers.get("Content-Length") or 0)
        done = 0
        while True:
            chunk = response.read(256 * 1024)
            if not chunk:
                break
            handle.write(chunk)
            done += len(chunk)
            if total:
                log(f"\r  {done / 1e6:6.1f} / {total / 1e6:6.1f} MB", end="")
    tmp.replace(dest)
    log(f"\n  guardado em {dest} ({dest.stat().st_size / 1e6:.1f} MB)")
    return dest


def ensure_gtfs_zip(cache_dir: Path, refresh: bool = False) -> Path:
    zip_path = Path(cache_dir) / "gtfs.zip"
    if refresh or not zip_path.exists():
        download_file(GTFS_STATIC_URL, zip_path)
    return zip_path


def connect(db_path: Path) -> sqlite3.Connection:
    con = sqlite3.connect(str(db_path), timeout=30)
    con.row_factory = sqlite3.Row
    return con


def _csv_rows(zf: zipfile.ZipFile, name: str) -> Iterable[dict[str, str]]:
    with zf.open(name) as raw:
        yield from csv.DictReader(io.TextIOWrapper(raw, encoding="utf-8-sig"))


def _insert_many(con: sqlite3.Connection, sql: str, rows: Iterable[tuple], batch: int = 20000) -> int:
    total = 0
    chunk: list[tuple] = []
    for row in rows:
        chunk.append(row)
        if len(chunk) >= batch:
            con.executemany(sql, chunk)
            total += len(chunk)
            chunk.clear()
    if chunk:
        con.executemany(sql, chunk)
        total += len(chunk)
    return total


SCHEMA = """
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE agency (
  agency_id TEXT, agency_name TEXT, agency_url TEXT, agency_timezone TEXT,
  agency_lang TEXT, agency_phone TEXT, agency_fare_url TEXT, agency_email TEXT
);
CREATE TABLE routes (
  route_id TEXT PRIMARY KEY, agency_id TEXT, route_short_name TEXT, route_long_name TEXT,
  route_desc TEXT, route_type INTEGER, route_url TEXT, route_color TEXT, route_text_color TEXT
);
CREATE TABLE stops (
  stop_id TEXT PRIMARY KEY, stop_code TEXT, stop_name TEXT, stop_desc TEXT,
  stop_lat REAL, stop_lon REAL, zone_id TEXT, stop_url TEXT, location_type INTEGER,
  parent_station TEXT, stop_timezone TEXT, wheelchair_boarding INTEGER
);
CREATE TABLE trips (
  trip_id TEXT PRIMARY KEY, route_id TEXT, service_id TEXT, trip_headsign TEXT,
  trip_short_name TEXT, direction_id INTEGER, block_id TEXT, shape_id TEXT,
  wheelchair_accessible INTEGER, bikes_allowed INTEGER
);
CREATE TABLE stop_times (
  trip_id TEXT, arrival_time TEXT, departure_time TEXT, stop_id TEXT,
  stop_sequence INTEGER, stop_headsign TEXT, pickup_type INTEGER, drop_off_type INTEGER,
  shape_dist_traveled REAL, timepoint INTEGER, arrival_sec INTEGER, departure_sec INTEGER
);
CREATE TABLE calendar (
  service_id TEXT, monday INTEGER, tuesday INTEGER, wednesday INTEGER, thursday INTEGER,
  friday INTEGER, saturday INTEGER, sunday INTEGER, start_date TEXT, end_date TEXT
);
CREATE TABLE calendar_dates (service_id TEXT, date TEXT, exception_type INTEGER);
CREATE TABLE shapes (
  shape_id TEXT, shape_pt_lat REAL, shape_pt_lon REAL, shape_pt_sequence INTEGER,
  shape_dist_traveled REAL
);
CREATE TABLE feed_info (
  feed_publisher_name TEXT, feed_publisher_url TEXT, feed_lang TEXT,
  feed_start_date TEXT, feed_end_date TEXT, feed_version TEXT
);
"""

INDEXES = """
CREATE INDEX idx_stop_times_stop_dep ON stop_times(stop_id, departure_sec);
CREATE INDEX idx_stop_times_stop_seq ON stop_times(stop_id, stop_sequence);
CREATE INDEX idx_stop_times_trip_seq ON stop_times(trip_id, stop_sequence);
CREATE INDEX idx_trips_route ON trips(route_id);
CREATE INDEX idx_trips_service ON trips(service_id);
CREATE INDEX idx_trips_shape ON trips(shape_id);
CREATE INDEX idx_calendar_dates_date ON calendar_dates(date, service_id);
CREATE INDEX idx_shapes_shape_seq ON shapes(shape_id, shape_pt_sequence);
CREATE INDEX idx_routes_short ON routes(route_short_name);
"""


def import_gtfs(zip_path: Path, db_path: Path, force: bool = False) -> dict[str, Any]:
    zip_path = Path(zip_path)
    db_path = Path(db_path)
    if not zip_path.exists():
        raise FileNotFoundError(zip_path)

    with zipfile.ZipFile(zip_path) as zf:
        feed_info = next(iter(_csv_rows(zf, "feed_info.txt")), {})
        feed_version = feed_info.get("feed_version", "")

    if db_path.exists() and not force:
        try:
            con = connect(db_path)
            row = con.execute("SELECT value FROM meta WHERE key='feed_version'").fetchone()
            con.close()
            if row and row[0] == feed_version:
                log(f"  base de dados já está no feed {feed_version}")
                return load_meta(db_path)
        except Exception:
            pass

    tmp_path = db_path.with_suffix(".tmp")
    if tmp_path.exists():
        tmp_path.unlink()
    db_path.parent.mkdir(parents=True, exist_ok=True)
    log(f"  a importar {zip_path.name} para {db_path.name} (pode demorar ~1 min)")

    con = sqlite3.connect(str(tmp_path))
    con.executescript(
        "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-200000;"
    )
    con.executescript(SCHEMA)

    started = time.time()
    with zipfile.ZipFile(zip_path) as zf:
        # agency
        con.executemany(
            "INSERT INTO agency VALUES (?,?,?,?,?,?,?,?)",
            [
                (
                    r.get("agency_id", ""), r.get("agency_name", ""), r.get("agency_url", ""),
                    r.get("agency_timezone", ""), r.get("agency_lang", ""), r.get("agency_phone", ""),
                    r.get("agency_fare_url", ""), r.get("agency_email", ""),
                )
                for r in _csv_rows(zf, "agency.txt")
            ],
        )
        # routes
        n = _insert_many(
            con,
            "INSERT OR REPLACE INTO routes VALUES (?,?,?,?,?,?,?,?,?)",
            (
                (
                    r.get("route_id", ""), r.get("agency_id", ""), r.get("route_short_name", ""),
                    r.get("route_long_name", ""), r.get("route_desc", ""), int_or(r.get("route_type")),
                    r.get("route_url", ""), r.get("route_color", ""), r.get("route_text_color", ""),
                )
                for r in _csv_rows(zf, "routes.txt")
            ),
        )
        log(f"    routes: {n}")
        # stops
        n = _insert_many(
            con,
            "INSERT OR REPLACE INTO stops VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
            (
                (
                    r.get("stop_id", ""), r.get("stop_code", ""), r.get("stop_name", ""),
                    r.get("stop_desc", ""), float_or(r.get("stop_lat")), float_or(r.get("stop_lon")),
                    r.get("zone_id", ""), r.get("stop_url", ""), int_or(r.get("location_type")),
                    r.get("parent_station", ""), r.get("stop_timezone", ""),
                    int_or(r.get("wheelchair_boarding")),
                )
                for r in _csv_rows(zf, "stops.txt")
            ),
        )
        log(f"    stops: {n}")
        # trips
        n = _insert_many(
            con,
            "INSERT OR REPLACE INTO trips VALUES (?,?,?,?,?,?,?,?,?,?)",
            (
                (
                    r.get("trip_id", ""), r.get("route_id", ""), r.get("service_id", ""),
                    r.get("trip_headsign", ""), r.get("trip_short_name", ""),
                    int_or(r.get("direction_id")), r.get("block_id", ""), r.get("shape_id", ""),
                    int_or(r.get("wheelchair_accessible")), int_or(r.get("bikes_allowed")),
                )
                for r in _csv_rows(zf, "trips.txt")
            ),
        )
        log(f"    trips: {n}")
        # stop_times (o ficheiro grande)
        n = _insert_many(
            con,
            "INSERT INTO stop_times VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
            (
                (
                    r.get("trip_id", ""), r.get("arrival_time", ""), r.get("departure_time", ""),
                    r.get("stop_id", ""), int_or(r.get("stop_sequence")), r.get("stop_headsign", ""),
                    int_or(r.get("pickup_type")), int_or(r.get("drop_off_type")),
                    float_or(r.get("shape_dist_traveled")), int_or(r.get("timepoint")),
                    parse_gtfs_time(r.get("arrival_time", "")),
                    parse_gtfs_time(r.get("departure_time", "")),
                )
                for r in _csv_rows(zf, "stop_times.txt")
            ),
            batch=25000,
        )
        log(f"    stop_times: {n}")
        # calendar
        n = _insert_many(
            con,
            "INSERT INTO calendar VALUES (?,?,?,?,?,?,?,?,?,?)",
            (
                (
                    r.get("service_id", ""), int_or(r.get("monday")), int_or(r.get("tuesday")),
                    int_or(r.get("wednesday")), int_or(r.get("thursday")), int_or(r.get("friday")),
                    int_or(r.get("saturday")), int_or(r.get("sunday")),
                    r.get("start_date", ""), r.get("end_date", ""),
                )
                for r in _csv_rows(zf, "calendar.txt")
            ),
        )
        log(f"    calendar: {n}")
        # calendar_dates
        n = _insert_many(
            con,
            "INSERT INTO calendar_dates VALUES (?,?,?)",
            (
                (r.get("service_id", ""), r.get("date", ""), int_or(r.get("exception_type")))
                for r in _csv_rows(zf, "calendar_dates.txt")
            ),
        )
        log(f"    calendar_dates: {n}")
        # shapes
        n = _insert_many(
            con,
            "INSERT INTO shapes VALUES (?,?,?,?,?)",
            (
                (
                    r.get("shape_id", ""), float_or(r.get("shape_pt_lat")),
                    float_or(r.get("shape_pt_lon")), int_or(r.get("shape_pt_sequence")),
                    float_or(r.get("shape_dist_traveled")),
                )
                for r in _csv_rows(zf, "shapes.txt")
            ),
            batch=25000,
        )
        log(f"    shapes: {n}")
        # feed_info
        con.executemany(
            "INSERT INTO feed_info VALUES (?,?,?,?,?,?)",
            [
                (
                    r.get("feed_publisher_name", ""), r.get("feed_publisher_url", ""),
                    r.get("feed_lang", ""), r.get("feed_start_date", ""),
                    r.get("feed_end_date", ""), r.get("feed_version", ""),
                )
                for r in _csv_rows(zf, "feed_info.txt")
            ],
        )

    log("  a criar índices…")
    con.executescript(INDEXES)
    counts = {}
    for table in ("routes", "stops", "trips", "stop_times", "calendar", "calendar_dates", "shapes"):
        counts[table] = con.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
    meta = {
        "feed_version": feed_version,
        "feed_start_date": feed_info.get("feed_start_date", ""),
        "feed_end_date": feed_info.get("feed_end_date", ""),
        "imported_at": now_lisbon().isoformat(),
        "counts": counts,
    }
    con.executemany(
        "INSERT OR REPLACE INTO meta VALUES (?,?)",
        [(key, json.dumps(value, ensure_ascii=False) if isinstance(value, (dict, list)) else str(value)) for key, value in meta.items()],
    )
    con.commit()
    con.close()
    tmp_path.replace(db_path)
    log(f"  importado em {time.time() - started:.1f}s -> {db_path}")
    return load_meta(db_path)


def load_meta(db_path: Path) -> dict[str, Any]:
    con = connect(db_path)
    out: dict[str, Any] = {}
    for row in con.execute("SELECT key, value FROM meta"):
        value = row["value"]
        try:
            out[row["key"]] = json.loads(value)
        except Exception:
            out[row["key"]] = value
    con.close()
    return out


def active_services(con: sqlite3.Connection, day: date) -> set[str]:
    ymd = day.strftime("%Y%m%d")
    # Não depender do locale do sistema (em pt_PT %A devolve "segunda-feira").
    weekday = ("monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday")[
        day.weekday()
    ]
    rows = con.execute(
        f"SELECT service_id FROM calendar WHERE start_date <= ? AND end_date >= ? AND {weekday} = 1",
        (ymd, ymd),
    ).fetchall()
    active = {r[0] for r in rows if r[0]}
    for row in con.execute("SELECT service_id, exception_type FROM calendar_dates WHERE date = ?", (ymd,)):
        sid, kind = row[0], row[1]
        if not sid:
            continue
        if kind == 1:
            active.add(sid)
        elif kind == 2:
            active.discard(sid)
    return active


def load_stops(con: sqlite3.Connection) -> list[dict[str, Any]]:
    stops = []
    for row in con.execute(
        "SELECT stop_id, stop_code, stop_name, stop_lat, stop_lon FROM stops "
        "WHERE stop_lat != 0 AND stop_lon != 0"
    ):
        name = row["stop_name"] or row["stop_id"]
        stops.append(
            {
                "id": str(row["stop_id"]),
                "number": str(row["stop_code"] or row["stop_id"]),
                "name": name,
                "lat": float(row["stop_lat"]),
                "lng": float(row["stop_lon"]),
                "norm": normalize(name),
                "tokens": search_tokens(name),
            }
        )
    stops.sort(key=lambda s: s["name"])
    return stops


def route_summaries(con: sqlite3.Connection) -> tuple[dict[str, dict[str, Any]], dict[str, str]]:
    rows = con.execute(
        """
        SELECT r.route_id, r.route_short_name, r.route_long_name, r.route_color,
               COUNT(t.trip_id) AS trips,
               GROUP_CONCAT(DISTINCT t.direction_id) AS directions
        FROM routes r
        LEFT JOIN trips t ON t.route_id = r.route_id
        GROUP BY r.route_id
        """
    ).fetchall()
    by_number: dict[str, dict[str, Any]] = {}
    by_route_id: dict[str, str] = {}
    for row in rows:
        number = (row["route_short_name"] or row["route_id"] or "").strip()
        if not number:
            continue
        by_route_id[row["route_id"]] = number
        directions = {d for d in str(row["directions"] or "").split(",") if d in ("0", "1")}
        direction = "Ida" if directions == {"0"} else "Volta" if directions == {"1"} else "Ida/Volta"
        entry = by_number.setdefault(
            number,
            {
                "number": number,
                "name": row["route_long_name"] or number,
                "routeId": row["route_id"],
                "direction": direction,
                "color": row["route_color"] or "",
                "variants": [],
                "_trips": -1,
            },
        )
        entry["variants"].append(
            {
                "id": row["route_id"],
                "headsign": row["route_long_name"] or number,
                "direction": direction,
                "trips": row["trips"],
            }
        )
        if row["trips"] > entry["_trips"]:
            entry["_trips"] = row["trips"]
            entry["name"] = row["route_long_name"] or number
            entry["routeId"] = row["route_id"]
            entry["direction"] = direction
    for entry in by_number.values():
        entry.pop("_trips", None)
        entry["variants"].sort(key=lambda v: -int(v.get("trips") or 0))
    return by_number, by_route_id


def route_directions(con: sqlite3.Connection, number: str) -> list[dict[str, Any]]:
    """Sentidos reais da carreira, com origem/destino legíveis.

    A app original expõe apenas "Ida"/"Volta", o que não diz para onde vai o
    autocarro. Aqui escolhemos, para cada direction_id, o par origem/destino
    mais frequente nas viagens GTFS. Assim os botões podem mostrar, por exemplo,
    "Ida · Sapadores → Pontinha Centro".
    """
    rows = con.execute(
        """
        WITH ordered AS (
            SELECT t.trip_id, t.direction_id, st.stop_sequence, s.stop_name,
                   ROW_NUMBER() OVER (PARTITION BY t.trip_id ORDER BY st.stop_sequence) AS rn_first,
                   ROW_NUMBER() OVER (PARTITION BY t.trip_id ORDER BY st.stop_sequence DESC) AS rn_last
            FROM trips t
            JOIN routes r ON r.route_id = t.route_id
            JOIN stop_times st ON st.trip_id = t.trip_id
            JOIN stops s ON s.stop_id = st.stop_id
            WHERE r.route_short_name = ?
        ), ends AS (
            SELECT trip_id, direction_id,
                   MAX(CASE WHEN rn_first = 1 THEN stop_name END) AS origin,
                   MAX(CASE WHEN rn_last = 1 THEN stop_name END) AS destination
            FROM ordered
            GROUP BY trip_id
        )
        SELECT direction_id, origin, destination, COUNT(*) AS trips
        FROM ends
        GROUP BY direction_id, origin, destination
        ORDER BY direction_id, trips DESC
        """,
        (number,),
    ).fetchall()

    best: dict[int, sqlite3.Row] = {}
    for row in rows:
        direction_id = int(row["direction_id"] or 0)
        best.setdefault(direction_id, row)

    out: list[dict[str, Any]] = []
    for direction_id in sorted(best):
        row = best[direction_id]
        origin = str(row["origin"] or number)
        destination = str(row["destination"] or origin)
        circular = origin == destination
        if circular:
            label = f"Circulação · {origin}"
            short = "Circulação"
        else:
            label = f"{origin} → {destination}"
            # Nos botões do mapa o espaço é curto; remove apenas sufixos entre
            # parênteses, mantendo o destino completo no label/popup.
            short = re.sub(r"\s*\([^)]*\)\s*$", "", destination).strip() or destination
        out.append(
            {
                "direction": "Ida" if direction_id == 0 else "Volta",
                "directionId": direction_id,
                "origin": origin,
                "destination": destination,
                "label": label,
                "shortLabel": short,
                "circular": circular,
                "trips": int(row["trips"] or 0),
            }
        )
    return out


def _in_clause(values: Iterable[str]) -> tuple[str, list[str]]:
    values = list(values)
    return ",".join("?" for _ in values), values


def _departure_rows(
    con: sqlite3.Connection,
    stop_id: str,
    base_day: date,
    active: set[str],
    min_sec: int,
    limit: int,
) -> list[sqlite3.Row]:
    if not active:
        return []
    placeholders, params = _in_clause(active)
    sql = f"""
        SELECT st.trip_id, st.stop_id, st.departure_sec, st.arrival_sec, st.stop_sequence,
               t.route_id, t.direction_id, t.trip_headsign, t.service_id,
               r.route_short_name, r.route_long_name
        FROM stop_times st
        JOIN trips t ON t.trip_id = st.trip_id
        JOIN routes r ON r.route_id = t.route_id
        WHERE st.stop_id = ? AND st.departure_sec >= ? AND t.service_id IN ({placeholders})
        ORDER BY st.departure_sec
        LIMIT ?
    """
    try:
        return con.execute(sql, [stop_id, min_sec, *params, limit]).fetchall()
    except sqlite3.OperationalError:
        return []


def stop_departures(
    con: sqlite3.Connection,
    stop: dict[str, Any],
    current: datetime,
    vehicles_by_trip: dict[str, dict[str, Any]] | None = None,
    limit: int = 10,
) -> list[dict[str, Any]]:
    active_today = active_services(con, current.date())
    active_yesterday = active_services(con, current.date() - timedelta(days=1))
    active_tomorrow = active_services(con, current.date() + timedelta(days=1))
    now_sec = current.hour * 3600 + current.minute * 60 + current.second
    midnight = current.replace(hour=0, minute=0, second=0, microsecond=0)

    candidates: list[tuple[datetime, sqlite3.Row]] = []
    for base_day, active, min_sec in (
        (current.date(), active_today, now_sec),
        (current.date() - timedelta(days=1), active_yesterday, 86400),
        (current.date() + timedelta(days=1), active_tomorrow, 0),
    ):
        base = datetime.combine(base_day, dtime.min, tzinfo=current.tzinfo)
        for row in _departure_rows(con, stop["id"], base_day, active, min_sec, 60):
            dep_sec = int(row["departure_sec"] or 0)
            when = base + timedelta(seconds=dep_sec)
            if when >= current:
                candidates.append((when, row))
    candidates.sort(key=lambda item: item[0])

    out: list[dict[str, Any]] = []
    seen: set[tuple[str, str, str]] = set()
    for when, row in candidates:
        trip_id = row["trip_id"]
        route = row["route_short_name"] or row["route_id"] or ""
        key = (trip_id, stop["id"], when.isoformat())
        if key in seen:
            continue
        seen.add(key)
        scheduled_sec = max(0, int((when - current).total_seconds()))
        eta_sec = scheduled_sec
        realtime = False
        vehicle_id = ""
        vehicle = (vehicles_by_trip or {}).get(trip_id)
        if vehicle and vehicle.get("lat") is not None:
            seq = vehicle.get("currentStopSequence")
            stop_seq = int(row["stop_sequence"] or 0)
            if not seq or int(seq) <= stop_seq:
                distance = haversine_m(
                    float(vehicle["lat"]), float(vehicle["lng"]), stop["lat"], stop["lng"]
                )
                speed = float(vehicle.get("speed") or 0)
                if speed < 4.0 or speed > 30.0:
                    speed = 5.5
                eta = distance / speed
                if 0 <= eta <= 7200:
                    eta_sec = int(eta)
                    realtime = True
                    vehicle_id = str(vehicle.get("vehicleId") or "")
        minutes, seconds = divmod(max(0, eta_sec), 60)
        out.append(
            {
                "routeNumber": route,
                "headsign": row["trip_headsign"] or row["route_long_name"] or route,
                "destination": row["route_long_name"] or row["trip_headsign"] or route,
                "minutes": minutes,
                "seconds": seconds,
                "time": when.isoformat(),
                "scheduled": when.isoformat(),
                "realtime": realtime,
                "vehicleId": vehicle_id,
                "stopId": stop["id"],
                "stopName": stop["name"],
                "tripId": trip_id,
                "direction": "Ida" if int(row["direction_id"] or 0) == 0 else "Volta",
                "etaSeconds": eta_sec,
            }
        )
    # O tempo real pode reordenar as chegadas; ordena pelo ETA efetivo.
    out.sort(key=lambda item: (item["etaSeconds"], item["time"]))
    return out[:limit]


def stop_routes(con: sqlite3.Connection, stop_id: str, day: date) -> list[dict[str, Any]]:
    active = active_services(con, day)
    if not active:
        return []
    placeholders, params = _in_clause(active)
    rows = con.execute(
        f"""
        SELECT DISTINCT t.route_id
        FROM stop_times st JOIN trips t ON t.trip_id = st.trip_id
        WHERE st.stop_id = ? AND t.service_id IN ({placeholders})
        """,
        [stop_id, *params],
    ).fetchall()
    by_number, by_route_id = route_summaries(con)
    numbers = {by_route_id.get(r[0]) for r in rows}
    return [by_number[n] for n in sorted(numbers, key=lambda x: (len(x), x)) if n and n in by_number]


def stop_times_for(
    con: sqlite3.Connection,
    route_number: str,
    direction: str,
    stop_id: str,
    day: date,
) -> list[dict[str, Any]]:
    active = active_services(con, day)
    if not active:
        return []
    placeholders, params = _in_clause(active)
    sql = f"""
        SELECT st.departure_time, st.departure_sec, t.direction_id, r.route_short_name
        FROM stop_times st
        JOIN trips t ON t.trip_id = st.trip_id
        JOIN routes r ON r.route_id = t.route_id
        WHERE st.stop_id = ? AND r.route_short_name = ? AND t.service_id IN ({placeholders})
        ORDER BY st.departure_sec
        LIMIT 300
    """
    rows = con.execute(sql, [stop_id, route_number, *params]).fetchall()
    out = []
    for row in rows:
        direction_value = "Ida" if int(row["direction_id"] or 0) == 0 else "Volta"
        if direction and direction not in ("", "all", "todas"):
            wanted = direction.lower()
            if wanted in ("0", "ida") and direction_value != "Ida":
                continue
            if wanted in ("1", "volta") and direction_value != "Volta":
                continue
        dep = (row["departure_time"] or "")[:5]
        out.append(
            {
                "routeNumber": row["route_short_name"] or route_number,
                "time": dep,
                "direction": direction_value,
                "stopId": stop_id,
            }
        )
    return out


def shape_for_route(
    con: sqlite3.Connection, route_number: str, direction: str = ""
) -> dict[str, Any]:
    routes = con.execute(
        "SELECT route_id FROM routes WHERE route_short_name = ?", (route_number,)
    ).fetchall()
    if not routes:
        return {"routeNumber": route_number, "points": [], "stops": []}
    route_ids = [r[0] for r in routes]
    placeholders, params = _in_clause(route_ids)
    direction_filter = ""
    if direction.lower() in ("0", "ida"):
        direction_filter = " AND t.direction_id = 0"
    elif direction.lower() in ("1", "volta"):
        direction_filter = " AND t.direction_id = 1"
    shape_row = con.execute(
        f"""
        SELECT t.shape_id, COUNT(*) AS c
        FROM trips t
        WHERE t.route_id IN ({placeholders}) AND t.shape_id != '' {direction_filter}
        GROUP BY t.shape_id ORDER BY c DESC LIMIT 1
        """,
        params,
    ).fetchone()
    if not shape_row:
        return {"routeNumber": route_number, "points": [], "stops": []}
    shape_id = shape_row["shape_id"]
    points = [
        [float(r["shape_pt_lat"]), float(r["shape_pt_lon"])]
        for r in con.execute(
            "SELECT shape_pt_lat, shape_pt_lon FROM shapes WHERE shape_id = ? ORDER BY shape_pt_sequence",
            (shape_id,),
        )
    ]
    trip = con.execute(
        f"SELECT trip_id FROM trips WHERE route_id IN ({placeholders}) AND shape_id = ? LIMIT 1",
        [*params, shape_id],
    ).fetchone()
    stops = []
    if trip:
        for r in con.execute(
            """
            SELECT s.stop_id, s.stop_name, s.stop_lat, s.stop_lon, st.stop_sequence
            FROM stop_times st JOIN stops s ON s.stop_id = st.stop_id
            WHERE st.trip_id = ? ORDER BY st.stop_sequence
            """,
            (trip["trip_id"],),
        ):
            stops.append(
                {
                    "id": str(r["stop_id"]),
                    "name": r["stop_name"],
                    "lat": float(r["stop_lat"]),
                    "lng": float(r["stop_lon"]),
                }
            )
    return {
        "routeNumber": route_number,
        "shapeId": shape_id,
        "points": points,
        "stops": stops,
    }


def nearest_stops(
    stops: list[dict[str, Any]], lat: float, lon: float, limit: int = 6, max_m: float = 900
) -> list[dict[str, Any]]:
    ranked = sorted(
        ((haversine_m(lat, lon, s["lat"], s["lng"]), s) for s in stops),
        key=lambda item: item[0],
    )
    return [dict(s, distance_m=round(d, 1)) for d, s in ranked[:limit] if d <= max_m]


def suggest_stops(stops: list[dict[str, Any]], query: str, limit: int = 10) -> list[dict[str, Any]]:
    q_norm = normalize(query)
    q_tokens = search_tokens(query)
    if not q_norm:
        return []
    ranked: list[tuple[float, dict[str, Any]]] = []
    for stop in stops:
        score = 0.0
        if stop["norm"] == q_norm:
            score = 100
        elif stop["norm"].startswith(q_norm):
            score = 80
        elif q_norm in stop["norm"]:
            score = 60
        elif q_tokens and q_tokens.issubset(stop["tokens"]):
            score = 50 + len(q_tokens) * 2
        else:
            overlap = len(q_tokens & stop["tokens"])
            if overlap:
                score = 20 + overlap * 5
        if score:
            ranked.append((score, stop))
    ranked.sort(key=lambda item: (-item[0], item[1]["name"]))
    return [
        {
            "id": s["id"],
            "name": s["name"],
            "lat": s["lat"],
            "lng": s["lng"],
            "type": "stop",
            "score": score,
        }
        for score, s in ranked[:limit]
    ]
