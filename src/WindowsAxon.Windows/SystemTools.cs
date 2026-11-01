using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WindowsAxon.Agent.Tools;

namespace WindowsAxon.Windows;

public sealed class SystemTools
{
    internal static object[] Query(string query, string scope = "root\\cimv2")
    {
        using var search = new ManagementObjectSearcher(scope, query);
        using var results = search.Get();
        return results.Cast<ManagementBaseObject>().Select(item =>
        {
            using (item)
            {
                return item.Properties.Cast<PropertyData>().ToDictionary(property => property.Name,
                                                                         property => property.Value);
            }
        }).ToArray();
    }

    [Tool("system_get_info",
          "system",
          "Read computer name, domain or workgroup, signed-in user, Windows version, CPU, RAM, uptime, and computer model.")]
    public object Info() => new
    {
        OS = Query("SELECT Caption,Version,LastBootUpTime,TotalVisibleMemorySize FROM Win32_OperatingSystem"),
        CPU = Query("SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor"),
        Computer = Query("SELECT Name,Domain,PartOfDomain,Workgroup,UserName,Manufacturer,Model FROM Win32_ComputerSystem")
    };

    [Tool("system_get_performance",
          "system",
          "Read current CPU and memory usage and top five processes by CPU and working memory.")]
    public object Performance()
    {
        var processes = Query("SELECT Name,IDProcess,PercentProcessorTime,WorkingSet FROM Win32_PerfFormattedData_PerfProc_Process WHERE IDProcess<>0")
            .Cast<Dictionary<string, object>>()
            .ToArray();
        return new

        {
            CPU = Query("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"),
            Memory = Query("SELECT FreePhysicalMemory,TotalVisibleMemorySize FROM Win32_OperatingSystem"),
            TopCpu = processes.OrderByDescending(p => Convert.ToUInt64(p["PercentProcessorTime"])).Take(5).ToArray(),
            TopMemory = processes.OrderByDescending(p => Convert.ToUInt64(p["WorkingSet"])).Take(5).ToArray()
        };
    }

    [Tool("disk_get_usage",
          "system",
          "Read total, used, and free space on ready drives.")]
    public object Disks() => DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => new
    {
        drive.Name,
        drive.TotalSize,
        Free = drive.AvailableFreeSpace,
        Used = drive.TotalSize - drive.TotalFreeSpace
    })
        .ToArray();

    [Tool("process_list",
          "system",
          "List processes, optionally filtering by name. Maximum 50 results.")]
    public object Processes(string name = "") => Query("SELECT ProcessId,Name,WorkingSetSize FROM Win32_Process")
        .Cast<Dictionary<string, object>>().Where(p => p["Name"].ToString()!.Contains(name,
                                                                                      StringComparison.OrdinalIgnoreCase))
        .Take(50)
        .ToArray();

    [Tool("process_stop",
          "system",
          "Stop a process owned by the current Windows user. Never stops WindowsAxon, Windows security, or a protected process. No undo.",
          RiskLevel.Danger)]
    public string Stop(int processId)
    {
        using var process = Process.GetProcessById(processId);
        UiSafety.CheckProcess(process.ProcessName);
        if (processId == Environment.ProcessId || new[] {
            "consent",
            "CredentialUIBroker",
            "lsass",
            "winlogon",
            "MsMpEng",
            "System"
        }.Contains(process.ProcessName,
                   StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("This process cannot be stopped.");
        }

        using var management = new ManagementObject($"Win32_Process.Handle='{processId}'");
        using var owner = management.InvokeMethod("GetOwnerSid", null, null);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        if (owner is null || Convert.ToUInt32(owner["ReturnValue"]) != 0 || owner["Sid"]?.ToString() != sid)
        {
            throw new UnauthorizedAccessException("Only current-user processes may be stopped.");
        }

        process.Kill();
        if (!process.WaitForExit(5000))
        {
            throw new InvalidOperationException("The process did not exit.");
        }

        return "Process exit verified.";
    }

    [Tool("service_list",
          "system",
          "Read service names, states, and start modes. Maximum 50 matching services.")]
    public object Services(string name = "") => Query("SELECT Name,DisplayName,State,StartMode FROM Win32_Service")
        .Cast<Dictionary<string, object>>()
        .Where(p => p["Name"].ToString()!.Contains(name, StringComparison.OrdinalIgnoreCase)
            || p["DisplayName"].ToString()!.Contains(name,
                                                     StringComparison.OrdinalIgnoreCase))
        .Take(50)
        .ToArray();

    [Tool("net_get_config",
          "network",
          "Read active network adapters, addresses, gateways, and DNS servers.")]
    public object Network() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
        .Select(adapter =>
        new
        {
            adapter.Name,
            Addresses = adapter.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString()).ToArray(),
            Gateway = adapter.GetIPProperties().GatewayAddresses.Select(a => a.Address.ToString()).ToArray(),
            DNS = adapter.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToArray()
        })
        .ToArray();

    [Tool("net_ping",
          "network",
          "Ping one hostname or IP address, with a three-second timeout.")]
    public async Task<object> Ping(string host, CancellationToken cancellationToken)
    {
        using var ping = new System.Net.NetworkInformation.Ping();
        var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(3), cancellationToken: cancellationToken);
        return new
        {
            Status = reply.Status.ToString(),
            Address = reply.Address?.ToString(),
            reply.RoundtripTime
        };
    }

    [Tool("net_dns_lookup",
          "network",
          "Resolve a hostname to IP addresses.")]
    public async Task<object> DnsLookup(string name, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(name, cancellationToken);
        return addresses.Select(address => address.ToString()).ToArray();
    }

    [Tool("net_test_port",
          "network",
          "Test whether a TCP port is reachable, with a five-second timeout.")]
    public async Task<string> Port(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, timeout.Token);
        return $"TCP connection succeeded: {host}:{port}.";
    }

    [Tool("eventlog_list_logs",
          "eventlog",
          "List Windows event log names and record counts. Inaccessible logs report an unavailable count.")]
    public object Logs(CancellationToken cancellationToken = default)
    {
        using var session = new EventLogSession();
        return session.GetLogNames().Select(name =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            long? count = null;
            try
            {
                count = session.GetLogInformation(name, PathType.LogName).RecordCount;
            }
            catch (Exception error) when (error is EventLogException or UnauthorizedAccessException)
            {
                // Keep the log in the list with an unknown count when its metadata cannot be read.
            }
            return new
            {
                Name = name,
                RecordCount = count
            };
        }).ToArray();
    }

    [Tool("eventlog_query",
          "eventlog",
          "Read up to 50 events, optionally filtered by numeric level, source, or after ISO timestamp. Security log may require administrator rights.")]
    public object Events(string logName = "Application",
                         int? level = null,
                         string? source = null,
                         DateTimeOffset? after = null,
                         DateTimeOffset? before = null,
                         CancellationToken cancellationToken = default)
    {
        if (level is < 1 or > 5)
        {
            throw new ArgumentException("level must be 1 through 5.");
        }

        var conditions = new List<string>();
        if (level is not null)
        {
            conditions.Add($"Level={level}");
        }

        if (after is not null)
        {
            conditions.Add($"TimeCreated[@SystemTime>='{after.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}']");
        }

        if (before is not null)
        {
            conditions.Add($"TimeCreated[@SystemTime<='{before.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}']");
        }

        if (after > before)
        {
            throw new ArgumentException("after must precede before.");
        }

        string query = conditions.Count == 0 ? "*" : "*[System[" + string.Join(" and ", conditions) + "]]";
        using EventLogReader reader = new(new EventLogQuery(logName, PathType.LogName, query) { ReverseDirection = true });
        List<object> events = new();

        for (var scanned = 0; scanned < 1000 && events.Count < 50; scanned++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var entry = reader.ReadEvent();
            if (entry is null)
            {
                break;
            }

            if (source is not null && !string.Equals(entry.ProviderName, source, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            events.Add(new
            {
                entry.Id,
                entry.Level,
                entry.ProviderName,
                entry.TimeCreated,
                Message = entry.FormatDescription()
            });
        }
        return events;
    }

    [Tool("printer_list",
          "printers",
          "Read installed printer names, statuses, default printer, ports, and drivers.")]
    public object Printers() => Query("SELECT Name,PrinterStatus,Default,PortName,DriverName FROM Win32_Printer");

    [Tool("print_queue_list",
          "printers",
          "List print jobs for a printer.")]
    public object Jobs(string printer) => Query("SELECT Name,JobId,Owner,Document,Status FROM Win32_PrintJob")
        .Cast<Dictionary<string, object>>()
        .Where(job => job["Name"].ToString()!.StartsWith(printer + ",", StringComparison.OrdinalIgnoreCase)).ToArray();

    [Tool("print_job_cancel",
          "printers",
          "Cancel one print job, or all jobs owned by the current user on the named printer. Other users' jobs are refused.",
          RiskLevel.Change)]
    public string CancelJob(string printer, int? jobId = null)
    {
        using var search = new ManagementObjectSearcher("SELECT * FROM Win32_PrintJob");
        using var jobs = search.Get();
        var count = 0;

        foreach (ManagementObject job in jobs)
        {
            using (job)
            {
                if (!job["Name"].ToString()!.StartsWith(printer + ",", StringComparison.OrdinalIgnoreCase)
                    || jobId is not null
                    && Convert.ToInt32(job["JobId"]) != jobId)
                {
                    continue;
                }

                var owner = job["Owner"].ToString()!;

                if (!owner.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase)
                    && !owner.Equals($"{Environment.UserDomainName}\\{Environment.UserName}",
                                     StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Only current-user print jobs may be cancelled.");
                }

                job.Delete();
                count++;
            }
        }
        return $"Cancelled {count} jobs.";
    }

    [Tool("printer_print_test_page",
          "printers",
          "Ask Windows to print one test page on the named installed printer.",
          RiskLevel.Change)]
    public string TestPage(string printer)
    {
        using var search = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
        using var printers = search.Get();

        foreach (ManagementObject item in printers)
        {
            using (item)
            {
                if (!string.Equals(item["Name"].ToString(), printer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = Convert.ToUInt32(item.InvokeMethod("PrintTestPage", null));
                if (result != 0)
                {
                    throw new InvalidOperationException($"PrintTestPage failed: {result}.");
                }

                return "Windows accepted the test-page print request. Physical printing has not been verified.";
            }
        }
        throw new ArgumentException("Printer is not installed.");
    }
}
