namespace OhmGraphite
{
    public class PrometheusConfig
    {
        public int Port { get; }
        public string Host { get; }
        public string PushgatewayUrl { get; }
        public string Job { get; }

        public PrometheusConfig(int port, string host, string pushgateway_url, string job)
        {
            Port = port;
            Host = host;
            PushgatewayUrl = pushgateway_url;
            Job = job;
        }

        internal static PrometheusConfig ParseAppSettings(IAppConfig config)
        {
            string job = config["job"] ?? "ohmgraphite";
            string pushgateway_url = config["pushgateway_url"] ?? null;
            string host = config["prometheus_host"] ?? "*";
            if (!int.TryParse(config["prometheus_port"], out int port))
            {
                port = 4445;
            }
            return new PrometheusConfig(port, host, pushgateway_url, job);
        }
    }
}
