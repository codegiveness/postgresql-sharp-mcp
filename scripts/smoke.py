#!/usr/bin/env python3
"""Consumer-level JSON-RPC smoke against an isolated Docker PostgreSQL fixture."""
import argparse
import asyncio
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def docker_sql(container, database, sql):
    subprocess.run(["docker", "exec", "-i", container, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", database],
                   input=sql, text=True, check=True, stdout=subprocess.DEVNULL)


def seed(container):
    docker_sql(container, "postgres", """
CREATE ROLE mcp_reader LOGIN PASSWORD 'reader-disposable';
CREATE ROLE mcp_writer LOGIN PASSWORD 'writer-disposable';
GRANT pg_read_all_stats TO mcp_reader;
CREATE DATABASE tenant_a;
CREATE DATABASE tenant_b;
CREATE DATABASE tenant_denied;
REVOKE CONNECT ON DATABASE tenant_denied FROM PUBLIC;
GRANT CONNECT ON DATABASE tenant_a, tenant_b TO mcp_reader, mcp_writer;
""")
    for database, marker in [("tenant_a", "A_ONLY"), ("tenant_b", "B_ONLY")]:
        docker_sql(container, database, f"""
CREATE TABLE marker(value text NOT NULL);
INSERT INTO marker VALUES ('{marker}');
CREATE TABLE orders(id integer PRIMARY KEY, customer integer NOT NULL, note text DEFAULT 'sample');
INSERT INTO orders SELECT g,g%1000,'row-'||g FROM generate_series(1,20000) g;
CREATE INDEX orders_customer_duplicate_1 ON orders(customer);
CREATE INDEX orders_customer_duplicate_2 ON orders(customer);
CREATE TABLE tuning(id integer NOT NULL, customer integer NOT NULL);
INSERT INTO tuning SELECT g,g%1000 FROM generate_series(1,50000) g;
ANALYZE orders; ANALYZE tuning;
CREATE TABLE secret(value text);
INSERT INTO secret VALUES ('not-granted');
CREATE SCHEMA app;
CREATE VIEW app.marker_view AS SELECT value FROM public.marker;
CREATE FUNCTION app.lookup(n integer) RETURNS integer LANGUAGE sql AS $$SELECT n$$;
CREATE FUNCTION app.lookup(n text) RETURNS text LANGUAGE sql AS $$SELECT n$$;
CREATE PROCEDURE app.noop(n integer) LANGUAGE sql AS $$SELECT n$$;
CREATE SEQUENCE app.counter;
CREATE FUNCTION app.touch() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN RETURN NEW; END$$;
CREATE TRIGGER orders_touch BEFORE INSERT ON orders FOR EACH ROW EXECUTE FUNCTION app.touch();
GRANT USAGE ON SCHEMA public,app TO mcp_reader,mcp_writer;
GRANT SELECT ON marker,orders,tuning,app.marker_view TO mcp_reader,mcp_writer;
GRANT USAGE,SELECT ON SEQUENCE app.counter TO mcp_reader,mcp_writer;
GRANT INSERT,UPDATE,DELETE ON marker TO mcp_writer;
GRANT CREATE ON SCHEMA public TO mcp_writer;
""")
    docker_sql(container, "tenant_a", """
CREATE SCHEMA extensions;
CREATE EXTENSION pg_stat_statements WITH SCHEMA extensions;
CREATE EXTENSION hypopg WITH SCHEMA extensions;
GRANT USAGE ON SCHEMA extensions TO mcp_reader;
SELECT value AS a_workload_marker FROM marker;
""")
    docker_sql(container, "tenant_b", "SELECT value AS b_workload_marker FROM marker;")


class Mcp:
    def __init__(self, dll, env):
        self.dll, self.env = dll, env
        self.pending, self.next_id = {}, 0
        self.stderr = b""

    async def __aenter__(self):
        self.process = await asyncio.create_subprocess_exec("dotnet", str(self.dll), env=self.env,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        self.reader = asyncio.create_task(self.read_loop())
        self.errors = asyncio.create_task(self.process.stderr.read())
        init = await self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "postgresql-sharp-mcp-smoke", "version": "1"}})
        assert "result" in init, init
        await self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return self

    async def read_loop(self):
        try:
            while line := await self.process.stdout.readline():
                message = json.loads(line)
                if "id" in message and message["id"] in self.pending:
                    self.pending.pop(message["id"]).set_result(message)
        except Exception as exc:
            for future in self.pending.values():
                if not future.done(): future.set_exception(exc)
        finally:
            for future in self.pending.values():
                if not future.done(): future.set_exception(RuntimeError("MCP process exited before responding"))

    async def send(self, value):
        self.process.stdin.write((json.dumps(value, separators=(",", ":")) + "\n").encode())
        await self.process.stdin.drain()

    async def request(self, method, params):
        self.next_id += 1
        future = asyncio.get_running_loop().create_future()
        self.pending[self.next_id] = future
        await self.send({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params})
        return await asyncio.wait_for(future, 20)

    async def call(self, tool, **arguments):
        envelope = await self.request("tools/call", {"name": tool, "arguments": arguments})
        assert "result" in envelope, envelope
        result = envelope["result"]
        text = result["content"][0]["text"]
        assert len(text.encode()) <= 4096, (tool, len(text.encode()))
        data = json.loads(text)
        assert data == result["structuredContent"], result
        return result.get("isError", False), data

    async def ok(self, tool, **arguments):
        error, result = await self.call(tool, **arguments)
        assert not error, (tool, arguments, result)
        return result

    async def fails(self, tool, code=None, sqlstate=None, **arguments):
        error, result = await self.call(tool, **arguments)
        assert error, (tool, arguments, result)
        if code: assert result["error"]["code"] == code, result
        if sqlstate: assert result["error"]["sql_state"] == sqlstate, result
        if "database" in arguments: assert result["database"] == arguments["database"], result
        return result

    async def __aexit__(self, *args):
        self.process.stdin.close()
        try:
            await asyncio.wait_for(self.process.wait(), 5)
        except asyncio.TimeoutError:
            self.process.terminate()
            await self.process.wait()
        await self.reader
        self.stderr = await self.errors
        if args[0]: print(self.stderr.decode(), file=sys.stderr)
        assert self.process.returncode == 0, self.stderr.decode()


def rows(page):
    return [dict(zip([column["name"] for column in page["columns"]], row)) for row in page["rows"]]


async def verify(dll, container, port):
    base = f"Host=127.0.0.1;Port={port};Username=mcp_reader;Password=reader-disposable;Database="
    targets = {"a": base + "tenant_a", "a_copy": base + "tenant_a", "b": base + "tenant_b", "denied": base + "tenant_denied",
               "missing": base + "does_not_exist", "bad_auth": base.replace("reader-disposable", "wrong") + "tenant_a",
               "offline": "Host=127.0.0.1;Port=1;Username=mcp_reader;Password=reader-disposable;Database=tenant_a"}
    env = {k: v for k, v in os.environ.items() if not k.startswith("POSTGRES_")}
    env.update(POSTGRES_TARGETS=json.dumps(targets), POSTGRES_POOL_SIZE="2", POSTGRES_MAX_CONCURRENT_CALLS="8",
               POSTGRES_MAX_RESULT_BYTES="4096", POSTGRES_MAX_CELL_CHARS="256", POSTGRES_MAX_ROWS="50", POSTGRES_QUERY_TIMEOUT="3")
    async with Mcp(dll, env) as m:
        tools = (await m.request("tools/list", {}))["result"]["tools"]
        expected = {"list_databases", "list_schemas", "list_objects", "get_object_details", "execute_sql", "explain_query",
                    "get_top_queries", "analyze_indexes", "analyze_db_health"}
        assert {tool["name"] for tool in tools} == expected
        for tool in tools:
            if tool["name"] != "list_databases": assert "database" in tool["inputSchema"]["required"], tool
        assert next(t for t in tools if t["name"] == "execute_sql")["annotations"]["readOnlyHint"] is True
        assert (await m.ok("list_databases"))["targets"] == sorted(targets)
        discovered, offset = [], 0
        while True:
            page = await m.ok("list_databases", limit=2, offset=offset)
            discovered += page["targets"]
            if "next_offset" not in page: break
            offset = page["next_offset"]
        assert discovered == sorted(targets)
        missing_arg = await m.request("tools/call", {"name": "execute_sql", "arguments": {"sql": "SELECT 1"}})
        assert "error" in missing_arg or missing_arg["result"]["isError"] is True
        print(f"PASS shared 9-tool set ({len(json.dumps(tools, separators=(',', ':')).encode())} definition bytes), explicit database required, restricted annotations")
        calls = []
        for index in range(24):
            alias, db, marker = ("a", "tenant_a", "A_ONLY") if index % 2 == 0 else ("b", "tenant_b", "B_ONLY")
            calls.append((alias, db, marker, m.ok("execute_sql", database=alias,
                sql="SELECT current_database() AS db, value, pg_backend_pid() AS pid FROM marker CROSS JOIN LATERAL (SELECT pg_sleep(0.03)) s")))
        results = await asyncio.gather(*(call[3] for call in calls))
        pids = {"a": set(), "b": set()}
        for (alias, db, marker, _), result in zip(calls, results):
            assert result["database"] == alias
            record = rows(result)[0]
            assert record["db"] == db and record["value"] == marker, record
            pids[alias].add(record["pid"])
        assert not (pids["a"] & pids["b"]), pids
        assert all(len(values) <= 2 for values in pids.values()), pids
        print(f"PASS 24 concurrent alternating calls; isolated backend pools: {pids}")
        alias_calls = await asyncio.gather(*(m.ok("execute_sql", database="a_copy", sql="SELECT current_database(),value,pg_backend_pid() FROM marker") for _ in range(8)))
        assert all(result["database"] == "a_copy" and result["rows"][0][:2] == ["tenant_a","A_ONLY"] and result["rows"][0][2] in pids["a"] for result in alias_calls)
        print("PASS equivalent aliases reuse the same bounded pool")
        await m.fails("execute_sql", "invalid_target", database="unconfigured", sql="SELECT 1")
        await m.fails("execute_sql", "postgresql_error", "42501", database="denied", sql="SELECT 1")
        await m.fails("execute_sql", "postgresql_error", "3D000", database="missing", sql="SELECT 1")
        await m.fails("execute_sql", "postgresql_error", "28P01", database="bad_auth", sql="SELECT 1")
        await m.fails("execute_sql", "connection_error", database="offline", sql="SELECT 1")
        await m.fails("execute_sql", "postgresql_error", "42501", database="a", sql="SELECT * FROM secret")
        assert rows(await m.ok("execute_sql", database="b", sql="SELECT value FROM marker"))[0]["value"] == "B_ONLY"
        print("PASS unknown, denied, nonexistent, wrong credentials, offline; no fallback; table permissions")
        for alias in ("a", "b"):
            seen = []
            offset = 0
            while True:
                page = await m.ok("execute_sql", database=alias, sql="SELECT g FROM generate_series(1,11) g ORDER BY g", limit=3, offset=offset)
                seen += [row[0] for row in page["rows"]]
                if "next_offset" not in page:
                    assert page["truncated"] is False
                    break
                assert page["truncated"] is True and page["truncation_reason"] == "row_limit"
                offset = page["next_offset"]
            assert seen == list(range(1,12)), seen
        byte_sql = "SELECT g,g*2,g*3,g*4,g*5,g*6,g*7,g*8 FROM generate_series(1000000000::bigint,1000000500::bigint) g ORDER BY g"
        page = await m.ok("execute_sql", database="a", sql=byte_sql, limit=50)
        assert page["truncated"] and page["truncation_reason"] == "byte_limit", page
        next_page = await m.ok("execute_sql", database="a", sql=byte_sql, limit=50, offset=page["next_offset"])
        assert next_page["rows"][0][0] == page["rows"][-1][0] + 1
        for expression in ("repeat('x',100000)", "decode(repeat('61',100000),'hex')", "repeat('😀',1000)"):
            clipped = await m.ok("execute_sql", database="a", sql="SELECT " + expression + " AS value")
            assert clipped["clipped_cells"] == [{"row":0,"column":0}], clipped
            assert len(clipped["rows"][0][0]) <= 256
        await m.fails("execute_sql", "unsupported_result_type", database="a", sql="SELECT ARRAY[1,2]")
        await m.fails("execute_sql", "invalid_limit", database="a", sql="SELECT 1", limit=51)
        await m.fails("execute_sql", "invalid_offset", database="a", sql="SELECT 1", offset=-1)
        print("PASS row/byte pagination without omissions; explicit text/binary/Unicode clipping; limits")
        for sql in ("SELECT 1; SELECT 2", "/*outer /*nested*/ */ COMMIT", "ROLLBACK", "SET transaction_read_only=off", "DO $$BEGIN END$$", "SELECT 'bad"):
            await m.fails("execute_sql", "invalid_sql", database="a", sql=sql)
        await m.fails("execute_sql", "postgresql_error", "25006", database="a", sql="INSERT INTO marker VALUES ('BAD')")
        await m.fails("execute_sql", "postgresql_error", database="a", sql="WITH x AS (DELETE FROM marker RETURNING *) SELECT * FROM x")
        await m.fails("execute_sql", "postgresql_error", database="a", sql="SELECT set_config('transaction_read_only','off',true)")
        await m.fails("execute_sql", "read_only", database="a", sql="INSERT INTO marker VALUES ('BAD')", read_only=False)
        await m.fails("execute_sql", "postgresql_error", "42601", database="a", sql="SELECT FROM WHERE")
        await m.fails("execute_sql", "timeout", database="a", sql="SELECT pg_sleep(10)::text")
        quoted = await m.ok("execute_sql", database="a", sql="SELECT $$a;b$$ AS d, E'escaped\\\';still-string' AS e, 'ordinary;string' AS s; -- tail")
        assert quoted["rows"][0] == ["a;b", "escaped';still-string", "ordinary;string"]
        assert rows(await m.ok("execute_sql", database="a", sql="SELECT value FROM marker"))[0]["value"] == "A_ONLY"
        print("PASS single-statement guard, read-only DML/CTE protection, syntax errors, timeout and quoted SQL")
        schemas = rows(await m.ok("list_schemas", database="a", prefix="app"))
        assert [row["schema_name"] for row in schemas] == ["app"]
        objects = rows(await m.ok("list_objects", database="a", schema="public", type="table", limit=50))
        assert "secret" not in {row["name"] for row in objects}
        assert "orders" in {row["name"] for row in objects}
        empty = await m.ok("list_objects", database="a", schema="public' OR 1=1 --")
        assert empty["rows"] == []
        all_objects = []
        offset = 0
        while True:
            page = await m.ok("list_objects", database="a", schema="app", limit=2, offset=offset)
            all_objects += rows(page)
            if "next_offset" not in page: break
            offset = page["next_offset"]
        assert {o["type"] for o in all_objects} >= {"function", "procedure", "view", "sequence"}
        columns = await m.ok("get_object_details", database="a", schema="public", name="orders", section="columns", limit=2)
        assert [row["name"] for row in rows(columns["page"])] == ["id", "customer"]
        more = await m.ok("get_object_details", database="a", schema="public", name="orders", section="columns", limit=2, offset=columns["page"]["next_offset"])
        assert [row["name"] for row in rows(more["page"])] == ["note"]
        for section in ("constraints", "indexes", "triggers", "definition"):
            detail = await m.ok("get_object_details", database="a", schema="public", name="orders", section=section, limit=2)
            assert detail["section"] == section and detail["page"]["rows"], detail
        await m.ok("get_object_details", database="a", schema="app", name="marker_view", section="definition")
        await m.ok("get_object_details", database="a", schema="app", name="counter", section="definition")
        await m.fails("get_object_details", "ambiguous_object", database="a", schema="app", name="lookup", section="parameters")
        routine = next(o for o in all_objects if o["name"] == "lookup" and "integer" in o["identity_arguments"])
        for section in ("parameters", "definition"):
            await m.ok("get_object_details", database="a", schema="app", name="lookup", section=section, identity_arguments=routine["identity_arguments"])
        await m.ok("get_object_details", database="a", schema="app", name="noop", section="parameters")
        await m.fails("get_object_details", "object_not_found", database="a", schema="public", name="does_not_exist")
        await m.fails("get_object_details", "unsupported_section", database="a", schema="app", name="counter", section="columns")
        print("PASS focused schemas, paged objects/details, columns/constraints/indexes/triggers/definitions/routines, privilege filtering")
        for section in ("summary", "vacuum", "index", "constraints", "sequences", "replication", "blocking"):
            result = await m.ok("analyze_db_health", database="a", section=section, limit=2)
            assert result["database"] == "a" and result["section"] == section
        indexes = await m.ok("analyze_indexes", database="a", schema="public", table="orders", limit=3)
        duplicates = [r for r in rows(indexes["result"]) if r["index_name"].startswith("orders_customer_duplicate")]
        assert all(r["duplicate_count"] == 1 for r in duplicates) and len(duplicates) == 2, indexes
        workload = await m.ok("get_top_queries", database="a", limit=2)
        assert all("b_workload_marker" not in (r["query_text"] or "") for r in rows(workload["result"]))
        await m.fails("get_top_queries", "extension_missing", database="b")
        print("PASS all health sections, structural duplicate index evidence, database-filtered workload, missing extension")
        baseline = await m.ok("explain_query", database="a", sql="SELECT * FROM tuning WHERE customer=42")
        assert baseline["plan"]["total_cost"] > 0
        full = await m.ok("explain_query", database="a", sql="SELECT * FROM tuning WHERE customer=42", format="json")
        assert full["plan"][0]["Plan"]["Node Type"] == "Seq Scan"
        actual = await m.ok("explain_query", database="a", sql="SELECT * FROM orders WHERE id=1", analyze=True)
        assert actual["analyzed"] and actual["plan"]["execution_time_ms"] >= 0
        await m.fails("explain_query", "postgresql_error", "25006", database="a", sql="DELETE FROM marker", analyze=True)
        candidate = ["CREATE INDEX ON public.tuning(customer)"]
        what_if = await m.ok("explain_query", database="a", sql="SELECT * FROM tuning WHERE customer=42", indexes=candidate)
        assert what_if["comparison"]["with_indexes_total_cost"] < what_if["comparison"]["baseline_total_cost"], what_if
        assert what_if["hypothetical_indexes"], what_if
        await m.fails("explain_query", "extension_missing", database="b", sql="SELECT * FROM tuning WHERE customer=42", indexes=candidate)
        await m.fails("explain_query", "postgresql_error", database="a", sql="SELECT * FROM tuning WHERE customer=42", indexes=["CREATE INDEX ON public.no_such_table(customer)"])
        assert (await m.ok("explain_query", database="a", sql="SELECT * FROM tuning WHERE customer=42"))["plan"]["total_cost"] == baseline["plan"]["total_cost"]
        permanent = await m.ok("execute_sql", database="a", sql="SELECT count(*) AS n FROM pg_indexes WHERE tablename='tuning'")
        assert permanent["rows"] == [[0]], permanent
        assert rows(await m.ok("execute_sql", database="a", sql="SELECT value FROM marker"))[0]["value"] == "A_ONLY"
        print(f"PASS estimated/JSON/ANALYZE plans, HypoPG cost {what_if['comparison']['baseline_total_cost']} -> {what_if['comparison']['with_indexes_total_cost']}, cleanup after success/failure; no permanent index")
    writer_env = dict(env)
    writer_env["POSTGRES_ACCESS_MODE"] = "unrestricted"
    writer_env["POSTGRES_TARGETS"] = json.dumps({"a": f"Host=127.0.0.1;Port={port};Username=mcp_writer;Password=writer-disposable;Database=tenant_a"})
    async with Mcp(dll, writer_env) as m:
        tools = (await m.request("tools/list", {}))["result"]["tools"]
        execute = next(t for t in tools if t["name"] == "execute_sql")
        assert execute["annotations"]["readOnlyHint"] is False and execute["annotations"]["destructiveHint"] is True
        await m.fails("execute_sql", "postgresql_error", "25006", database="a", sql="INSERT INTO marker VALUES ('WRITE_OK')")
        mutation = await m.ok("execute_sql", database="a", sql="INSERT INTO marker VALUES ('WRITE_OK')", read_only=False)
        assert mutation["rows_affected"] == 1, mutation
        assert [r["value"] for r in rows(await m.ok("execute_sql", database="a", sql="SELECT value FROM marker ORDER BY value"))] == ["A_ONLY", "WRITE_OK"]
        await m.fails("execute_sql", "invalid_offset", database="a", sql="DELETE FROM marker RETURNING *", offset=1, read_only=False)
        await m.fails("execute_sql", "postgresql_error", "42501", database="a", sql="SELECT * FROM secret", read_only=False)
        await m.ok("execute_sql", database="a", sql="DELETE FROM marker WHERE value='WRITE_OK'", read_only=False)
        await m.ok("execute_sql", database="a", sql="CREATE TABLE mcp_write_test(id integer)", read_only=False)
        assert (await m.ok("execute_sql", database="a", sql="SELECT to_regclass('public.mcp_write_test')::text"))["rows"] == [["mcp_write_test"]]
        returning = await m.ok("execute_sql", database="a", sql="INSERT INTO mcp_write_test SELECT g FROM generate_series(1,10) g RETURNING id", limit=3, read_only=False)
        assert returning["truncated"] and "next_offset" not in returning, returning
        assert (await m.ok("execute_sql", database="a", sql="SELECT count(*) FROM mcp_write_test"))["rows"] == [[10]]
        await m.ok("execute_sql", database="a", sql="DROP TABLE mcp_write_test", read_only=False)
        print("PASS unrestricted annotations, explicit write opt-in, committed DML/DDL and truncated RETURNING, credential boundaries, no write replay pagination")
    validate_env = dict(env)
    validate_env["POSTGRES_TARGETS"] = json.dumps({key: targets[key] for key in ("a","b")})
    preflight = subprocess.run(["dotnet", str(dll), "--validate"], env=validate_env, capture_output=True, text=True)
    assert preflight.returncode == 0 and preflight.stdout == "", preflight
    checked = [json.loads(line) for line in preflight.stderr.splitlines()]
    assert {row["database"] for row in checked} == {"a","b"} and {row["rows"][0][0] for row in checked} == {"tenant_a","tenant_b"}, checked
    base_env = dict(env)
    del base_env["POSTGRES_TARGETS"]
    base_env["POSTGRES_CONNECTION_STRING"] = base + "not_the_selected_database"
    base_env["POSTGRES_DATABASES"] = json.dumps(["tenant_a","tenant_b"])
    async with Mcp(dll, base_env) as m:
        assert (await m.ok("execute_sql", database="tenant_b", sql="SELECT current_database(),value FROM marker"))["rows"] == [["tenant_b","B_ONLY"]]
    invalid_env = dict(env)
    invalid_env["POSTGRES_TARGETS"] = '{"a":"Host=localhost;Username=test"}'
    bad = subprocess.run(["dotnet", str(dll), "--validate"], env=invalid_env, capture_output=True, text=True)
    assert bad.returncode == 1 and bad.stdout == "", bad
    print("PASS --validate on both targets, base connection plus explicit allowlist, missing Database startup rejection")
    print("ALL MCP SMOKE SCENARIOS PASSED")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--container", required=True)
    parser.add_argument("--dll", type=Path, default=ROOT / "src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll")
    parser.add_argument("--seed", action="store_true")
    args = parser.parse_args()
    if args.seed: seed(args.container)
    endpoint = subprocess.check_output(["docker", "port", args.container, "5432/tcp"], text=True).strip()
    port = int(endpoint.rsplit(":", 1)[1])
    asyncio.run(verify(args.dll.resolve(), args.container, port))


if __name__ == "__main__": main()
