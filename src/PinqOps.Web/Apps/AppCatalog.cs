namespace PinqOps.Web;

/// <summary>One installable catalog app: everything needed for a docker run.</summary>
/// <param name="Unauthenticated">
/// True when the image has no usable authentication out of the box — its own
/// config file is the only way to add any. Such an app is safe only because
/// published ports bind to loopback by default; exposing one publicly hands it to
/// anyone who can reach the port. Surfaced in the UI so that is a decision rather
/// than a surprise.
/// </param>
public sealed record AppSpec(
    string Id,
    string Name,
    string Category,
    string Image,
    (int Host, int Container)[] Ports,
    string[] Env,
    (string Volume, string Path)[] Volumes,
    string? Cmd = null,
    string? Note = null,
    bool Unauthenticated = false);

/// <summary>
/// Curated one-click catalog of popular self-hosted services. Every entry is a
/// fixed, reviewed spec — the install endpoint only accepts catalog ids, so it
/// can never be used to run an arbitrary image. (Running an arbitrary image is
/// possible only through the separate admin-only create endpoint, which builds a
/// constrained argv: named volumes only, no host bind mounts, --privileged,
/// --cap-add, --device or host namespaces.) Containers are named pinqops-&lt;id&gt;
/// and labeled pinqops.app=&lt;id&gt; so the dashboard can track them. Default
/// credentials (change them!) are noted per app.
/// </summary>
public static class AppCatalog
{
    public const string ContainerPrefix = "pinqops-";
    public const string Label = "pinqops.app";

    /// <summary>
    /// All catalog apps join this user-defined network so they can reach each
    /// other by container name (the default bridge has no name DNS).
    /// </summary>
    public const string SharedNetwork = "pinqops-apps";

    public static readonly IReadOnlyList<AppSpec> Apps =
    [
        // --- Databases & caches ---
        new("redis", "Redis", "database", "redis:7-alpine", [(6379, 6379)], [], [("data", "/data")],
            Cmd: "redis-server --requirepass {{password}}", Note: "requirepass (generated password)"),
        new("keydb", "KeyDB", "database", "eqalpha/keydb:latest", [(6380, 6379)], [], [("data", "/data")],
            Cmd: "keydb-server --requirepass {{password}}", Note: "requirepass (generated password)"),
        new("memcached", "Memcached", "database", "memcached:alpine", [(11211, 11211)], [], [],
            Note: "no authentication — keep it on loopback", Unauthenticated: true),
        new("postgres", "PostgreSQL", "database", "postgres:16-alpine", [(5432, 5432)], ["POSTGRES_PASSWORD={{password}}"], [("data", "/var/lib/postgresql/data")], Note: "user: postgres (generated password)"),
        new("mysql", "MySQL", "database", "mysql:8", [(3306, 3306)], ["MYSQL_ROOT_PASSWORD={{password}}"], [("data", "/var/lib/mysql")], Note: "root (generated password)"),
        new("mariadb", "MariaDB", "database", "mariadb:11", [(3307, 3306)], ["MARIADB_ROOT_PASSWORD={{password}}"], [("data", "/var/lib/mysql")], Note: "root (generated password)"),
        new("mongo", "MongoDB", "database", "mongo:7", [(27017, 27017)],
            ["MONGO_INITDB_ROOT_USERNAME=root", "MONGO_INITDB_ROOT_PASSWORD={{password}}"], [("data", "/data/db")],
            Note: "user: root (generated password)"),
        new("couchdb", "CouchDB", "database", "couchdb:3", [(5984, 5984)], ["COUCHDB_USER=admin", "COUCHDB_PASSWORD={{password}}"], [("data", "/opt/couchdb/data")], Note: "user: admin (generated password)"),
        new("neo4j", "Neo4j", "database", "neo4j:5", [(7474, 7474), (7687, 7687)], ["NEO4J_AUTH=neo4j/{{password}}"], [("data", "/data")], Note: "user: neo4j (generated password)"),
        new("clickhouse", "ClickHouse", "database", "clickhouse/clickhouse-server:latest", [(8123, 8123)],
            ["CLICKHOUSE_USER=pinqops", "CLICKHOUSE_PASSWORD={{password}}"], [("data", "/var/lib/clickhouse")],
            Note: "user: pinqops (generated password)"),
        new("influxdb", "InfluxDB", "database", "influxdb:2", [(8086, 8086)],
            ["DOCKER_INFLUXDB_INIT_MODE=setup", "DOCKER_INFLUXDB_INIT_USERNAME=admin", "DOCKER_INFLUXDB_INIT_PASSWORD={{password}}", "DOCKER_INFLUXDB_INIT_ORG=pinqops", "DOCKER_INFLUXDB_INIT_BUCKET=default"],
            [("data", "/var/lib/influxdb2")], Note: "user: admin (generated password)"),
        new("questdb", "QuestDB", "database", "questdb/questdb:latest", [(9002, 9000)], [], [("data", "/var/lib/questdb")],
            Note: "open by default — keep it on loopback", Unauthenticated: true),
        new("cassandra", "Cassandra", "database", "cassandra:5", [(9042, 9042)], [], [("data", "/var/lib/cassandra")],
            Note: "authenticator is off by default — keep it on loopback", Unauthenticated: true),
        new("cockroachdb", "CockroachDB", "database", "cockroachdb/cockroach:latest", [(26257, 26257), (8081, 8080)], [], [("data", "/cockroach/cockroach-data")],
            Cmd: "start-single-node --insecure",
            Note: "--insecure single node (no auth, no TLS) — keep it on loopback", Unauthenticated: true),
        new("surrealdb", "SurrealDB", "database", "surrealdb/surrealdb:latest", [(8010, 8000)], [],
            [], Cmd: "start --user root --pass {{password}}", Note: "user: root (generated password)"),

        // --- Search, queues & messaging ---
        // Both ship a security layer that needs bootstrapping (users, TLS,
        // certificates) beyond what a one-click install can do, and OpenSearch
        // 2.12+ additionally demands a password with symbols that the generator
        // does not produce. They stay off, which loopback binding makes tolerable.
        new("elasticsearch", "Elasticsearch", "search-queue", "docker.elastic.co/elasticsearch/elasticsearch:8.17.0", [(9200, 9200)],
            ["discovery.type=single-node", "xpack.security.enabled=false", "ES_JAVA_OPTS=-Xms512m -Xmx512m"], [("data", "/usr/share/elasticsearch/data")],
            Note: "security disabled — keep it on loopback", Unauthenticated: true),
        new("opensearch", "OpenSearch", "search-queue", "opensearchproject/opensearch:2", [(9201, 9200)],
            ["discovery.type=single-node", "DISABLE_SECURITY_PLUGIN=true"], [("data", "/usr/share/opensearch/data")],
            Note: "security plugin disabled — keep it on loopback", Unauthenticated: true),
        new("meilisearch", "Meilisearch", "search-queue", "getmeili/meilisearch:latest", [(7700, 7700)], ["MEILI_MASTER_KEY={{password}}"], [("data", "/meili_data")], Note: "master key: generated"),
        new("typesense", "Typesense", "search-queue", "typesense/typesense:27.1", [(8108, 8108)], ["TYPESENSE_API_KEY={{password}}", "TYPESENSE_DATA_DIR=/data"], [("data", "/data")], Note: "api key: generated"),
        new("rabbitmq", "RabbitMQ", "search-queue", "rabbitmq:3-management", [(5672, 5672), (15672, 15672)],
            ["RABBITMQ_DEFAULT_USER=pinqops", "RABBITMQ_DEFAULT_PASS={{password}}"], [("data", "/var/lib/rabbitmq")],
            Note: "user: pinqops (generated password, ui: 15672)"),
        new("nats", "NATS", "search-queue", "nats:latest", [(4222, 4222), (8222, 8222)], [], [],
            Cmd: "--auth {{password}}", Note: "token auth (generated password)"),
        new("kafka", "Apache Kafka", "search-queue", "apache/kafka:latest", [(9092, 9092)], [], [],
            Note: "PLAINTEXT listener, no auth — keep it on loopback", Unauthenticated: true),
        new("mosquitto", "Mosquitto MQTT", "search-queue", "eclipse-mosquitto:2", [(1883, 1883)], [], [("data", "/mosquitto/data")],
            Cmd: "mosquitto -c /mosquitto-no-auth.conf",
            Note: "anonymous access (needs a password file for auth) — keep it on loopback", Unauthenticated: true),

        // --- Storage & web servers ---
        new("minio", "MinIO", "web-storage", "minio/minio:latest", [(9000, 9000), (9001, 9001)],
            ["MINIO_ROOT_USER=pinqops", "MINIO_ROOT_PASSWORD={{password}}"], [("data", "/data")], Cmd: "server /data --console-address :9001", Note: "user: pinqops (generated password, console: 9001)"),
        new("nginx", "Nginx", "web-storage", "nginx:alpine", [(8090, 80)], [], []),
        new("caddy", "Caddy", "web-storage", "caddy:2", [(8091, 80)], [], [("data", "/data")]),
        new("httpd", "Apache httpd", "web-storage", "httpd:alpine", [(8092, 80)], [], []),

        // --- DB admin tools ---
        new("adminer", "Adminer", "admin-tool", "adminer:latest", [(8083, 8080)], [], [],
            Note: "a database client for any reachable host — keep it on loopback", Unauthenticated: true),
        new("pgadmin", "pgAdmin", "admin-tool", "dpage/pgadmin4:latest", [(5050, 80)],
            ["PGADMIN_DEFAULT_EMAIL=admin@pinqops.local", "PGADMIN_DEFAULT_PASSWORD={{password}}"], [("data", "/var/lib/pgadmin")], Note: "user: admin@pinqops.local (generated password)"),
        // PMA_ARBITRARY=1 turns this into a database client for any host the
        // container can reach, which makes it an SSRF and lateral-movement tool
        // for anyone who reaches the page. Pointed at the catalog's MySQL instead.
        new("phpmyadmin", "phpMyAdmin", "admin-tool", "phpmyadmin:latest", [(8082, 80)],
            ["PMA_HOST=pinqops-mysql"], [], Note: "install the MySQL app too; sign in as root"),
        // Mongo Express takes its connection solely from this URL, and the MongoDB
        // the catalog installs runs with MONGO_INITDB_ROOT_PASSWORD set — so an
        // anonymous URL had every command refused and the container restarting for
        // ever while the install reported success. The cross-app token resolves to
        // that MongoDB's stored password on this environment, the same way WordPress
        // reaches the catalog's MySQL.
        new("mongo-express", "Mongo Express", "admin-tool", "mongo-express:latest", [(8093, 8081)],
            ["ME_CONFIG_MONGODB_URL=mongodb://root:{{password:mongo}}@pinqops-mongo:27017/?authSource=admin"], [],
            Note: "install the MongoDB app too"),
        new("redisinsight", "RedisInsight", "admin-tool", "redis/redisinsight:latest", [(5540, 5540)], [], [("data", "/data")]),

        // --- Monitoring ---
        new("grafana", "Grafana", "monitoring", "grafana/grafana:latest", [(3000, 3000)],
            ["GF_SECURITY_ADMIN_USER=admin", "GF_SECURITY_ADMIN_PASSWORD={{password}}"], [("data", "/var/lib/grafana")],
            Note: "user: admin (generated password)"),
        new("prometheus", "Prometheus", "monitoring", "prom/prometheus:latest", [(9090, 9090)], [], [("data", "/prometheus")],
            Note: "no auth (needs a web config file) — keep it on loopback", Unauthenticated: true),
        new("uptime-kuma", "Uptime Kuma", "monitoring", "louislam/uptime-kuma:1", [(3001, 3001)], [], [("data", "/app/data")],
            Note: "first visitor creates the admin account — reach it before anyone else"),
        new("netdata", "Netdata", "monitoring", "netdata/netdata:latest", [(19999, 19999)], [], [],
            Note: "dashboard is open by default — keep it on loopback", Unauthenticated: true),

        // --- Dev & CI ---
        new("gitea", "Gitea", "dev-ci", "gitea/gitea:latest", [(3002, 3000), (2222, 22)], [], [("data", "/data")]),
        new("jenkins", "Jenkins", "dev-ci", "jenkins/jenkins:lts", [(8084, 8080)], [], [("data", "/var/jenkins_home")]),
        new("code-server", "code-server", "dev-ci", "codercom/code-server:latest", [(8085, 8080)], ["PASSWORD={{password}}"], [("data", "/home/coder")], Note: "password: generated"),
        new("verdaccio", "Verdaccio", "dev-ci", "verdaccio/verdaccio:latest", [(4873, 4873)], [], [("data", "/verdaccio/storage")]),
        // SonarQube has no env for the initial password; it ships admin/admin and
        // forces a change at first login, so the only safe posture is loopback
        // until that has happened.
        new("sonarqube", "SonarQube", "dev-ci", "sonarqube:community", [(9003, 9000)], [], [("data", "/opt/sonarqube/data")],
            Note: "ships admin / admin — sign in and change it before exposing", Unauthenticated: true),

        // --- Auth & security ---
        new("keycloak", "Keycloak", "auth", "quay.io/keycloak/keycloak:latest", [(8880, 8080)],
            ["KEYCLOAK_ADMIN=admin", "KEYCLOAK_ADMIN_PASSWORD={{password}}"], [], Cmd: "start-dev", Note: "user: admin (generated password)"),
        new("vaultwarden", "Vaultwarden", "auth", "vaultwarden/server:latest", [(8087, 80)], [], [("data", "/data")]),

        // --- Mail ---
        // A real mail server, and everything that entails. It is here because the
        // relay pinqops sends through has to be *something*, and running your own is
        // the alternative to a provider — but it is the one catalog entry that does
        // not work the moment it starts. It needs its ports reachable from the
        // internet (loopback, the default here, receives nothing), a hostname that
        // matches its MX record, and the DNS records the Mail page generates. The
        // note says so rather than letting it look installed and be silent.
        new("docker-mailserver", "docker-mailserver", "mail", "mailserver/docker-mailserver:latest",
            [(25, 25), (587, 587), (993, 993)], ["ENABLE_FAIL2BAN=0", "SSL_TYPE="],
            [("mail-data", "/var/mail"), ("mail-state", "/var/mail-state"), ("mail-config", "/tmp/docker-mailserver")],
            Note: "needs public ports 25/587/993, a matching hostname and the DNS records on the Mail page — "
                + "it will not deliver until all three are done",
            Unauthenticated: true),

        // --- Applications ---
        new("wordpress", "WordPress", "app", "wordpress:latest", [(8088, 80)],
            ["WORDPRESS_DB_HOST=pinqops-mysql", "WORDPRESS_DB_USER=root", "WORDPRESS_DB_PASSWORD={{password:mysql}}", "WORDPRESS_DB_NAME=wordpress"],
            [("data", "/var/www/html")], Note: "install the MySQL app too"),
        new("ghost", "Ghost", "app", "ghost:5-alpine", [(2368, 2368)],
            ["NODE_ENV=development", "url=http://localhost:2368"], [("data", "/var/lib/ghost/content")]),
        new("nextcloud", "Nextcloud", "app", "nextcloud:apache", [(8089, 80)], [], [("data", "/var/www/html")]),
        new("jellyfin", "Jellyfin", "app", "jellyfin/jellyfin:latest", [(8096, 8096)], [], [("config", "/config"), ("cache", "/cache")]),
        new("navidrome", "Navidrome", "app", "deluan/navidrome:latest", [(4533, 4533)], [], [("data", "/data")]),
        new("syncthing", "Syncthing", "app", "syncthing/syncthing:latest", [(8384, 8384)], [], [("data", "/var/syncthing")]),
        new("n8n", "n8n", "app", "n8nio/n8n:latest", [(5678, 5678)], ["N8N_SECURE_COOKIE=false"], [("data", "/home/node/.n8n")]),
        new("nodered", "Node-RED", "app", "nodered/node-red:latest", [(1880, 1880)], [], [("data", "/data")]),
    ];

    public static AppSpec? Find(string id) =>
        Apps.FirstOrDefault(app => string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the spec's env entries, substituting credential tokens:
    /// <c>{{password}}</c> is this app's own password, <c>{{password:other}}</c>
    /// references another app's (e.g. WordPress → the MySQL root password).
    /// <paramref name="passwordForApp"/> supplies (and persists) the value per
    /// app id. Returns the final env list plus which entries carry credentials
    /// (env name → value) for display/storage.
    /// </summary>
    public static (IReadOnlyList<string> Env, IReadOnlyDictionary<string, string> Credentials) ResolveEnv(
        AppSpec spec,
        Func<string, string> passwordForApp)
    {
        var resolved = new List<string>(spec.Env.Length);
        var credentials = new Dictionary<string, string>();

        foreach (var entry in spec.Env)
        {
            var value = Substitute(entry, spec, passwordForApp, out var substituted);
            if (substituted)
            {
                var separator = entry.IndexOf('=');
                credentials[entry[..separator]] = value[(separator + 1)..];
            }

            resolved.Add(value);
        }

        return (resolved, credentials);
    }

    /// <summary>
    /// The spec's command with credential placeholders substituted, or null when
    /// it has none. Some images take their password only as a command-line flag
    /// (<c>redis-server --requirepass</c>, <c>nats --auth</c>), so without this
    /// they could only ship unauthenticated.
    /// </summary>
    public static string? ResolveCmd(AppSpec spec, Func<string, string> passwordForApp) =>
        spec.Cmd is null ? null : Substitute(spec.Cmd, spec, passwordForApp, out _);

    /// <summary>
    /// Replaces every <c>{{password}}</c> / <c>{{password:other}}</c> token in
    /// one value.
    /// </summary>
    private static string Substitute(
        string entry,
        AppSpec spec,
        Func<string, string> passwordForApp,
        out bool substituted)
    {
        var value = entry;
        substituted = false;

        // A loop rather than a single replace: one value may carry more than one
        // token, and a stale index after the first substitution would corrupt it.
        while (value.IndexOf("{{password", StringComparison.Ordinal) is var start && start >= 0)
        {
            var end = value.IndexOf("}}", start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException(
                    $"Malformed credential placeholder in catalog entry '{entry}'.");
            }

            var token = value[start..(end + 2)];
            var targetApp = token == "{{password}}" ? spec.Id : token["{{password:".Length..^2];
            value = value.Replace(token, passwordForApp(targetApp), StringComparison.Ordinal);
            substituted = true;
        }

        return value;
    }
}
