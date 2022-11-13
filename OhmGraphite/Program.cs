using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using LibreHardwareMonitor.Hardware;
using Prometheus;
using Topshelf;
using System.Net.Http;

namespace OhmGraphite
{
    internal class Program
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static void Main()
        {
            string configPath = string.Empty;
            bool showVersion = false;
            HostFactory.Run(x =>
            {
                x.Service<IManage>(s =>
                {
                    s.ConstructUsing(name =>
                    {
                        if (showVersion)
                        {
                            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
                            Console.WriteLine(version ?? "no version detected");
                            Environment.Exit(0);
                        }

                        var configDisplay = string.IsNullOrEmpty(configPath) ? "default" : configPath;
                        var config = Logger.LogFunction($"parse config {configDisplay}", () => MetricConfig.ParseAppSettings(CreateConfiguration(configPath)));

                        var computer = new Computer
                        {
                            IsGpuEnabled = config.EnabledHardware.Gpu,
                            IsMotherboardEnabled = config.EnabledHardware.Motherboard,
                            IsCpuEnabled = config.EnabledHardware.Cpu,
                            IsMemoryEnabled = config.EnabledHardware.Ram,
                            IsNetworkEnabled = config.EnabledHardware.Network,
                            IsStorageEnabled = config.EnabledHardware.Storage,
                            IsControllerEnabled = config.EnabledHardware.Controller,
                            IsPsuEnabled = config.EnabledHardware.Psu,
                            IsBatteryEnabled = config.EnabledHardware.Battery,
                        };
                        var collector = new SensorCollector(computer, config);
                        return CreateManager(config, collector);
                    });
                    s.WhenStarted(tc => tc.Start());
                    s.WhenStopped(tc => tc.Dispose());
                });

                // Allow one to specify a command line argument when running interactively
                x.AddCommandLineDefinition("config", v => configPath = v);
                x.AddCommandLineSwitch("version", v => showVersion = v);
                x.UseNLog();
                x.RunAsLocalSystem();
                x.SetDescription(
                    "Extract hardware sensor data and exports it to a given host and port in a graphite compatible format");
                x.SetDisplayName("Ohm Graphite");
                x.SetServiceName("OhmGraphite");
                x.OnException(ex => Logger.Error(ex, "OhmGraphite TopShelf encountered an error"));
            });
        }

        private static IAppConfig CreateConfiguration(string configPath)
        {
            if (string.IsNullOrEmpty(configPath))
            {
                // https://github.com/dotnet/runtime/issues/13051#issuecomment-510267727
                var processModule = Process.GetCurrentProcess().MainModule;
                if (processModule != null)
                {
                    var pt = processModule.FileName;
                    var fn = Path.Join(Path.GetDirectoryName(pt), "OhmGraphite.exe.config");
                    var configMap1 = new ExeConfigurationFileMap { ExeConfigFilename = fn };
                    var config1 = ConfigurationManager.OpenMappedExeConfiguration(configMap1, ConfigurationUserLevel.None);
                    return new CustomConfig(config1);
                }
            }

            if (!File.Exists(configPath))
            {
                throw new ApplicationException($"unable to detect config: ${configPath}");
            }

            var configMap = new ExeConfigurationFileMap { ExeConfigFilename = configPath };
            var config = ConfigurationManager.OpenMappedExeConfiguration(configMap, ConfigurationUserLevel.None);
            return new CustomConfig(config);
        }

        private static IManage CreateManager(MetricConfig config, SensorCollector collector)
        {
            var hostname = config.LookupName();
            double seconds = config.Interval.TotalSeconds;
            if (config.Graphite != null)
            {
                Logger.Info(
                    $"Graphite host: {config.Graphite.Host} port: {config.Graphite.Port} interval: {seconds} tags: {config.Graphite.Tags}");
                var writer = new GraphiteWriter(config.Graphite.Host,
                    config.Graphite.Port,
                    hostname,
                    config.Graphite.Tags);
                return new MetricTimer(config.Interval, collector, writer);
            }
            else if (config.Prometheus != null)
            {
                Logger.Info($"Prometheus port: {config.Prometheus.Port}");
                var registry = PrometheusCollection.SetupDefault(collector);
                var server = new MetricServer(config.Prometheus.Host, config.Prometheus.Port, registry: registry);
                if (config.Prometheus.PushgatewayUrl != null)
                {
                    Logger.Info($"Pushgateway url: {config.Prometheus.PushgatewayUrl}, Interval: {config.Interval.TotalSeconds}");
                    SendMetrics(config.Prometheus.Port, config.Prometheus.PushgatewayUrl, config.Interval, config.Prometheus.Job, config.LookupName());
                }
                return new PrometheusServer(server, collector);
            }
            else if (config.Timescale != null)
            {
                var writer = new TimescaleWriter(config.Timescale.Connection, config.Timescale.SetupTable, hostname);
                return new MetricTimer(config.Interval, collector, writer);
            }
            else if (config.Influx != null)
            {
                Logger.Info($"Influxdb address: {config.Influx.Address} db: {config.Influx.Db}");
                var writer = new InfluxWriter(config.Influx, hostname);
                return new MetricTimer(config.Interval, collector, writer);
            }
            else
            {
                Logger.Info($"Influx2 address: {config.Influx2.Options.Url}");
                var writer = new Influx2Writer(config.Influx2, hostname);
                return new MetricTimer(config.Interval, collector, writer);
            }
        }

        private static void SendMetrics(int port, string url, TimeSpan interval, string job, string hostname)
        {
            string metrics_url = $"http://127.0.0.1:{port}/metrics";
            string pushgateway_url = $"{url}metrics/job/{job}/instance/{hostname}";

            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        await Task.Delay(interval);
                        HttpClient client = new();
                        var metrics = client.GetAsync(metrics_url).Result.Content.ReadAsStringAsync().Result;
                        var data = new StringContent(metrics, System.Text.Encoding.UTF8, "text/plain");
                        await client.PostAsync(pushgateway_url, data);
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"Failed to send metrics: {ex}");
                    }
                }
            });
        }
    }
}