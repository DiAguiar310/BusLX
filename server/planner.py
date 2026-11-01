"""Planeador simples sobre o GTFS da Carris.

Estratégia:
1. paragens mais próximas da origem/destino;
2. viagens diretas (mesmo trip, paragem de origem antes do destino);
3. uma transferência (duas viagens, mesma paragem de interface);
4. fallback a pé.

Não é um motor multimodal completo; é um MVP determinístico e rápido para
a rede Carris, usando apenas o GTFS público.
"""
from __future__ import annotations

import math
import sqlite3
from datetime import date, datetime, time as dtime, timedelta
from typing import Any

from gtfs_store import active_services, haversine_m

WALK_SPEED_MPS = 1.35  # ~4,9 km/h
MIN_TRANSFER_SECONDS = 120


def _in_clause(values: list[str]) -> tuple[str, list[str]]:
    return ",".join("?" for _ in values), values


def _minutes(seconds: float) -> int:
    return max(0, int(math.ceil(seconds / 60)))


def _step(mode: str, instruction: str, route: str = "", minutes: int = 0) -> dict[str, Any]:
    return {
        "mode": mode,
        "instruction": instruction,
        "routeNumber": route,
        "durationMinutes": minutes,
    }


def _parse_when(value: str, current: datetime) -> datetime:
    if not value or value.lower() in ("now", "agora"):
        return current
    try:
        if "T" in value:
            parsed = datetime.fromisoformat(value)
            if parsed.tzinfo is None:
                parsed = parsed.replace(tzinfo=current.tzinfo)
            return parsed
        hh, mm = value.split(":")[:2]
        return current.replace(hour=int(hh), minute=int(mm), second=0, microsecond=0)
    except Exception:
        return current


def _direct(
    con: sqlite3.Connection,
    origin_stops: list[dict[str, Any]],
    dest_stops: list[dict[str, Any]],
    start: datetime,
    active: set[str],
    stops_by_id: dict[str, dict[str, Any]],
) -> list[dict[str, Any]]:
    if not active:
        return []
    o_ph, o_params = _in_clause([s["id"] for s in origin_stops])
    d_ph, d_params = _in_clause([s["id"] for s in dest_stops])
    a_ph, a_params = _in_clause(list(active))
    start_sec = start.hour * 3600 + start.minute * 60 + start.second
    sql = f"""
        SELECT st1.trip_id, st1.stop_id AS origin_stop, st1.departure_sec, st1.stop_sequence AS oseq,
               st2.stop_id AS dest_stop, st2.arrival_sec, st2.stop_sequence AS dseq,
               t.route_id, t.direction_id, t.trip_headsign, r.route_short_name, r.route_long_name
        FROM stop_times st1
        JOIN stop_times st2 ON st2.trip_id = st1.trip_id AND st2.stop_sequence > st1.stop_sequence
        JOIN trips t ON t.trip_id = st1.trip_id
        JOIN routes r ON r.route_id = t.route_id
        WHERE st1.stop_id IN ({o_ph}) AND st2.stop_id IN ({d_ph})
          AND t.service_id IN ({a_ph}) AND st1.departure_sec >= ?
        ORDER BY st1.departure_sec
        LIMIT 80
    """
    try:
        rows = con.execute(sql, [*o_params, *d_params, *a_params, start_sec]).fetchall()
    except sqlite3.OperationalError:
        return []

    base = datetime.combine(start.date(), dtime.min, tzinfo=start.tzinfo)
    candidates: list[dict[str, Any]] = []
    seen: set[tuple[str, int, str, str]] = set()
    for row in rows:
        origin_stop = stops_by_id.get(str(row["origin_stop"]))
        dest_stop = stops_by_id.get(str(row["dest_stop"]))
        if not origin_stop or not dest_stop:
            continue
        dep_sec = int(row["departure_sec"] or 0)
        arrival_sec = int(row["arrival_sec"] or 0)
        dep_dt = base + timedelta(seconds=dep_sec)
        if dep_dt < start:
            continue
        ride_sec = max(0, arrival_sec - dep_sec)
        if ride_sec <= 0:
            continue
        # escolhe a paragem de origem/destino mais próxima do utilizador
        origin_pick = min(
            origin_stops, key=lambda s: haversine_m(s["lat"], s["lng"], origin_stop["lat"], origin_stop["lng"])
        )
        dest_pick = min(
            dest_stops, key=lambda s: haversine_m(s["lat"], s["lng"], dest_stop["lat"], dest_stop["lng"])
        )
        walk_to = haversine_m(origin_pick["lat"], origin_pick["lng"], origin_stop["lat"], origin_stop["lng"]) / WALK_SPEED_MPS
        walk_from = haversine_m(dest_stop["lat"], dest_stop["lng"], dest_pick["lat"], dest_pick["lng"]) / WALK_SPEED_MPS
        ready = start + timedelta(seconds=walk_to)
        if dep_dt < ready:
            continue
        wait_sec = max(0, (dep_dt - ready).total_seconds())
        total = walk_to + wait_sec + ride_sec + walk_from
        route = row["route_short_name"] or row["route_id"] or ""
        key = (route, dep_sec, origin_stop["id"], dest_stop["id"])
        if key in seen:
            continue
        seen.add(key)
        headsign = row["trip_headsign"] or row["route_long_name"] or route
        steps = [
            _step("walk", f"Caminha até {origin_stop['name']} (~{_minutes(walk_to)} min)", minutes=_minutes(walk_to)),
        ]
        if wait_sec >= 60:
            steps.append(_step("wait", f"Espera ~{_minutes(wait_sec)} min na paragem {origin_stop['name']}", minutes=_minutes(wait_sec)))
        steps.append(
            _step(
                "bus",
                f"Apanha o {route} em direção a {headsign} ({origin_stop['name']} → {dest_stop['name']}, ~{_minutes(ride_sec)} min)",
                route,
                _minutes(ride_sec),
            )
        )
        steps.append(
            _step("walk", f"Caminha até ao destino (~{_minutes(walk_from)} min)", minutes=_minutes(walk_from))
        )
        candidates.append(
            {
                "summary": f"{route} · {_minutes(total)} min",
                "durationMinutes": _minutes(total),
                "departure": dep_dt.isoformat(),
                "arrival": (dep_dt + timedelta(seconds=ride_sec + walk_from)).isoformat(),
                "routeNumber": route,
                "steps": steps,
                "_total": total,
            }
        )
    candidates.sort(key=lambda c: c["_total"])
    for c in candidates:
        c.pop("_total", None)
    return candidates[:3]


def _one_transfer(
    con: sqlite3.Connection,
    origin_stops: list[dict[str, Any]],
    dest_stops: list[dict[str, Any]],
    start: datetime,
    active: set[str],
    stops_by_id: dict[str, dict[str, Any]],
) -> list[dict[str, Any]]:
    if not active:
        return []
    o_ph, o_params = _in_clause([s["id"] for s in origin_stops])
    d_ph, d_params = _in_clause([s["id"] for s in dest_stops])
    a_ph, a_params = _in_clause(list(active))
    start_sec = start.hour * 3600 + start.minute * 60 + start.second
    sql = f"""
        SELECT st1.trip_id AS trip1, st1.stop_id AS origin_stop, st1.departure_sec AS dep1,
               st1.stop_sequence AS oseq, stx.stop_id AS transfer_stop,
               stx.arrival_sec AS arrx, stx.stop_sequence AS xseq,
               stx2.trip_id AS trip2, stx2.departure_sec AS depx2,
               st2.stop_id AS dest_stop, st2.arrival_sec AS arr2, st2.stop_sequence AS dseq,
               t1.direction_id AS dir1, t1.trip_headsign AS head1,
               t2.direction_id AS dir2, t2.trip_headsign AS head2,
               r1.route_short_name AS route1, r1.route_long_name AS name1,
               r2.route_short_name AS route2, r2.route_long_name AS name2
        FROM stop_times st1
        JOIN stop_times stx ON stx.trip_id = st1.trip_id AND stx.stop_sequence > st1.stop_sequence
        JOIN stop_times stx2 ON stx2.stop_id = stx.stop_id AND stx2.trip_id != st1.trip_id
        JOIN stop_times st2 ON st2.trip_id = stx2.trip_id AND st2.stop_sequence > stx2.stop_sequence
        JOIN trips t1 ON t1.trip_id = st1.trip_id
        JOIN trips t2 ON t2.trip_id = stx2.trip_id
        JOIN routes r1 ON r1.route_id = t1.route_id
        JOIN routes r2 ON r2.route_id = t2.route_id
        WHERE st1.stop_id IN ({o_ph}) AND st2.stop_id IN ({d_ph})
          AND t1.service_id IN ({a_ph}) AND t2.service_id IN ({a_ph})
          AND st1.departure_sec >= ?
          AND stx2.departure_sec >= stx.arrival_sec + ?
          AND r1.route_id != r2.route_id
        ORDER BY st1.departure_sec
        LIMIT 30
    """
    try:
        rows = con.execute(
            sql,
            [*o_params, *d_params, *a_params, *a_params, start_sec, MIN_TRANSFER_SECONDS],
        ).fetchall()
    except sqlite3.OperationalError:
        return []

    base = datetime.combine(start.date(), dtime.min, tzinfo=start.tzinfo)
    candidates: list[dict[str, Any]] = []
    seen: set[tuple[str, str, int, str]] = set()
    for row in rows:
        origin_stop = stops_by_id.get(str(row["origin_stop"]))
        dest_stop = stops_by_id.get(str(row["dest_stop"]))
        transfer_stop = stops_by_id.get(str(row["transfer_stop"]))
        if not origin_stop or not dest_stop or not transfer_stop:
            continue
        dep1 = int(row["dep1"] or 0)
        dep_dt = base + timedelta(seconds=dep1)
        if dep_dt < start:
            continue
        ride1 = max(0, int(row["arrx"] or 0) - dep1)
        dep2 = int(row["depx2"] or 0)
        ride2 = max(0, int(row["arr2"] or 0) - dep2)
        if ride1 <= 0 or ride2 <= 0:
            continue
        walk_to = haversine_m(origin_stops[0]["lat"], origin_stops[0]["lng"], origin_stop["lat"], origin_stop["lng"]) / WALK_SPEED_MPS
        walk_from = haversine_m(dest_stop["lat"], dest_stop["lng"], dest_stops[0]["lat"], dest_stops[0]["lng"]) / WALK_SPEED_MPS
        ready = start + timedelta(seconds=walk_to)
        if dep_dt < ready:
            continue
        wait1 = max(0, (dep_dt - ready).total_seconds())
        transfer_start = dep_dt + timedelta(seconds=ride1)
        transfer_dt = base + timedelta(seconds=dep2)
        if transfer_dt < transfer_start:
            continue
        wait2 = max(0, (transfer_dt - transfer_start).total_seconds())
        total = walk_to + wait1 + ride1 + wait2 + ride2 + walk_from
        route1 = row["route1"] or ""
        route2 = row["route2"] or ""
        key = (route1, route2, dep1, transfer_stop["id"])
        if key in seen:
            continue
        seen.add(key)
        steps = [
            _step("walk", f"Caminha até {origin_stop['name']} (~{_minutes(walk_to)} min)", minutes=_minutes(walk_to)),
        ]
        if wait1 >= 60:
            steps.append(_step("wait", f"Espera ~{_minutes(wait1)} min na paragem {origin_stop['name']}", minutes=_minutes(wait1)))
        steps.append(
            _step(
                "bus",
                f"Apanha o {route1} em direção a {row['head1'] or row['name1']} até {transfer_stop['name']} (~{_minutes(ride1)} min)",
                route1,
                _minutes(ride1),
            )
        )
        steps.append(
            _step(
                "transfer",
                f"Muda para o {route2} em {transfer_stop['name']}"
                + (f" (espera ~{_minutes(wait2)} min)" if wait2 >= 60 else ""),
                route2,
                _minutes(wait2),
            )
        )
        steps.append(
            _step(
                "bus",
                f"Segue no {route2} até {dest_stop['name']} (~{_minutes(ride2)} min)",
                route2,
                _minutes(ride2),
            )
        )
        steps.append(_step("walk", f"Caminha até ao destino (~{_minutes(walk_from)} min)", minutes=_minutes(walk_from)))
        candidates.append(
            {
                "summary": f"{route1} → {route2} · {_minutes(total)} min",
                "durationMinutes": _minutes(total),
                "departure": dep_dt.isoformat(),
                "arrival": (base + timedelta(seconds=int(row["arr2"] or 0) + walk_from)).isoformat(),
                "routeNumber": route1,
                "steps": steps,
                "_total": total,
            }
        )
    candidates.sort(key=lambda c: c["_total"])
    for c in candidates:
        c.pop("_total", None)
    return candidates[:2]


def plan(
    con: sqlite3.Connection,
    stops: list[dict[str, Any]],
    origin: tuple[float, float],
    destination: tuple[float, float],
    when: str,
    current: datetime,
    language: str = "pt",
) -> list[dict[str, Any]]:
    start = _parse_when(when, current)
    origin_stops = sorted(
        (s for s in stops if haversine_m(origin[0], origin[1], s["lat"], s["lng"]) <= 900),
        key=lambda s: haversine_m(origin[0], origin[1], s["lat"], s["lng"]),
    )[:6]
    dest_stops = sorted(
        (s for s in stops if haversine_m(destination[0], destination[1], s["lat"], s["lng"]) <= 900),
        key=lambda s: haversine_m(destination[0], destination[1], s["lat"], s["lng"]),
    )[:6]
    if not origin_stops or not dest_stops:
        distance = haversine_m(origin[0], origin[1], destination[0], destination[1])
        if distance <= 3000:
            minutes = _minutes(distance / WALK_SPEED_MPS)
            return [
                {
                    "summary": f"A pé · {minutes} min",
                    "durationMinutes": minutes,
                    "steps": [_step("walk", f"Caminha até ao destino (~{minutes} min, {distance/1000:.1f} km)", minutes=minutes)],
                }
            ]
        return [
            {
                "summary": "Sem paragens próximas",
                "durationMinutes": 0,
                "steps": [_step("info", "Não encontrei paragens da Carris num raio de 900 m. Tenta uma morada mais próxima da rede.")],
            }
        ]

    stops_by_id = {s["id"]: s for s in stops}
    active = active_services(con, start.date())
    direct = _direct(con, origin_stops, dest_stops, start, active, stops_by_id)
    if direct:
        return direct
    transfer = _one_transfer(con, origin_stops, dest_stops, start, active, stops_by_id)
    if transfer:
        return transfer

    distance = haversine_m(origin[0], origin[1], destination[0], destination[1])
    if distance <= 3000:
        minutes = _minutes(distance / WALK_SPEED_MPS)
        return [
            {
                "summary": f"A pé · {minutes} min",
                "durationMinutes": minutes,
                "steps": [_step("walk", f"Caminha até ao destino (~{minutes} min, {distance/1000:.1f} km)", minutes=minutes)],
            }
        ]
    return [
        {
            "summary": "Sem ligação direta",
            "durationMinutes": 0,
            "steps": [
                _step("info", "Não encontrei ligação direta nem uma transferência simples para este horário. Tenta outra hora ou uma paragem próxima.")
            ],
        }
    ]
