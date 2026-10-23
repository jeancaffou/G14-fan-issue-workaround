using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

// Temperature-based fan control for the ASUS GA401IV.
internal static class G14FanGuard
{
    static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "G14FanGuard");
    static readonly string StopFile = Path.Combine(Data, "stop.request");
    static readonly string StatusFile = Path.Combine(Data, "status.json");
    static readonly string LogFile = Path.Combine(Data, "controller.log");
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    [DllImport("AsusWinIO64.dll")] static extern void InitializeWinIo();
    [DllImport("AsusWinIO64.dll")] static extern void ShutdownWinIo();
    [DllImport("AsusWinIO64.dll")] static extern int HealthyTable_FanCounts();
    [DllImport("AsusWinIO64.dll")] static extern void HealthyTable_SetFanIndex(byte index);
    [DllImport("AsusWinIO64.dll")] static extern int HealthyTable_FanRPM();
    [DllImport("AsusWinIO64.dll")] static extern void HealthyTable_SetFanTestMode(char mode);
    [DllImport("AsusWinIO64.dll")] static extern void HealthyTable_SetFanPwmDuty(short duty);
    [DllImport("AsusWinIO64.dll")] static extern ulong Thermal_Read_Cpu_Temperature();
    [DllImport(@"C:\Windows\System32\nvml.dll", CallingConvention=CallingConvention.Cdecl)] static extern int nvmlInit_v2();
    [DllImport(@"C:\Windows\System32\nvml.dll", CallingConvention=CallingConvention.Cdecl)] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr handle);
    [DllImport(@"C:\Windows\System32\nvml.dll", CallingConvention=CallingConvention.Cdecl)] static extern int nvmlDeviceGetTemperature(IntPtr handle, uint sensor, out uint temperature);
    [DllImport(@"C:\Windows\System32\nvml.dll", CallingConvention=CallingConvention.Cdecl)] static extern int nvmlShutdown();

    internal static int Percent(double c)
    {
        if (Double.IsNaN(c) || c < 10 || c > 120) throw new InvalidOperationException("Invalid temperature");
        if (c >= 90) return 100;
        if (c >= 85) return 75;
        if (c >= 80) return 50;
        if (c >= 75) return 35;
        if (c >= 70) return 25;
        if (c >= 60) return 20;
        if (c >= 50) return 15;
        return 0;
    }
    static int Ramp(int previous, int target)
    {
        // Do not ramp through low duties at which the fan may stall.
        if (target == 0) return 0;
        if (previous <= 0) return Math.Max(50, target);
        return target >= previous ? target : Math.Max(target, previous - 5);
    }
    static bool ValidRpm(int rpm, int duty, bool starting)
    {
        // Zero is expected while stopped; allow two cycles for a commanded restart.
        return rpm >= 0 && rpm <= 15000 && (duty == 0 || starting || rpm >= 500);
    }
    static bool Hot(double cpu, double acpi, double gpu) { return cpu >= 90 || acpi >= 90 || gpu >= 85; }
    static void Log(string message)
    {
        File.AppendAllText(LogFile, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
    }
    static void Beat(string path) { File.WriteAllText(path, DateTime.UtcNow.ToString("o")); }
    static bool Fresh(string path) { return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(10); }
    static void Automatic()
    {
        int count = HealthyTable_FanCounts();
        if (count != 2) throw new InvalidOperationException("Expected two ASUS diagnostic fans");
        for (byte fan = 0; fan < count; fan++)
        {
            HealthyTable_SetFanIndex(fan);
            HealthyTable_SetFanTestMode((char)0);
        }
    }
    static void ResetAutomatic()
    {
        InitializeWinIo();
        try { Automatic(); }
        finally { ShutdownWinIo(); }
    }
    static double AcpiTemperature()
    {
        double hottest = Double.NaN;
        using (var query = new ManagementObjectSearcher("root\\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
        using (var rows = query.Get())
        {
            foreach (ManagementObject row in rows)
            using (row)
            {
                double t = Convert.ToDouble(row["CurrentTemperature"], Inv) / 10.0 - 273.15;
                if (t < 10 || t > 120) throw new InvalidOperationException("Invalid ACPI thermal-zone reading");
                if (Double.IsNaN(hottest) || t > hottest) hottest = t;
            }
        }
        if (Double.IsNaN(hottest)) throw new InvalidOperationException("ACPI thermal zone unavailable");
        return hottest;
    }
    static int Worker(string guardianBeat, string workerBeat)
    {
        bool initialized = false, nvml = false;
        try
        {
            if (!Fresh(guardianBeat)) throw new InvalidOperationException("Guardian heartbeat unavailable");
            Beat(workerBeat);
            InitializeWinIo(); initialized = true;
            if (HealthyTable_FanCounts() != 2) throw new InvalidOperationException("Expected two ASUS diagnostic fans");
            if (nvmlInit_v2() != 0) throw new InvalidOperationException("NVIDIA temperature interface unavailable");
            nvml = true;
            IntPtr gpu;
            if (nvmlDeviceGetHandleByIndex_v2(0, out gpu) != 0) throw new InvalidOperationException("NVIDIA GPU unavailable");
            int cpuDuty = -1, gpuDuty = -1;
            int spinupCycles = 0;
            DateTime lastLoop = DateTime.UtcNow;
            while (!File.Exists(StopFile))
            {
                if (DateTime.UtcNow - lastLoop > TimeSpan.FromSeconds(8))
                    for (int resume=0;resume<5 && !Fresh(guardianBeat);resume++) Thread.Sleep(1000);
                lastLoop = DateTime.UtcNow;
                if (!Fresh(guardianBeat)) throw new InvalidOperationException("Guardian heartbeat expired");
                double cpu = Thermal_Read_Cpu_Temperature();
                double acpi = AcpiTemperature();
                uint gpuRaw;
                if (nvmlDeviceGetTemperature(gpu, 0, out gpuRaw) != 0) throw new InvalidOperationException("NVIDIA temperature read failed");
                double gpuTemp = gpuRaw;
                if (cpu < 10 || cpu > 120 || gpuTemp < 10 || gpuTemp > 120) throw new InvalidOperationException("Temperature sensor invalid");
                if (Hot(cpu, acpi, gpuTemp))
                {
                    Automatic(); cpuDuty = -1; gpuDuty = -1; spinupCycles = 0;
                    string hotState = String.Format(Inv,"{{\"time\":\"{0}\",\"mode\":\"FirmwareCoolingHighTemperature\",\"cpuC\":{1:F1},\"acpiC\":{2:F1},\"gpuC\":{3:F1}}}",DateTimeOffset.Now.ToString("o"),cpu,acpi,gpuTemp);
                    File.WriteAllText(StatusFile, hotState);
                    Beat(workerBeat);
                    Thread.Sleep(2000);
                    continue;
                }
                double controlTemp = Math.Max(Math.Max(cpu, acpi), gpuTemp);
                // Both fans use the hottest observed temperature because they share cooling pipes.
                int target = Percent(controlTemp);
                if (target > 0 && cpuDuty <= 0) spinupCycles = 2;
                if (target == 0) spinupCycles = 0;
                cpuDuty = Ramp(cpuDuty, target);
                gpuDuty = Ramp(gpuDuty, target);
                int[] rpm = new int[2];
                for (byte fan = 0; fan < 2; fan++)
                {
                    HealthyTable_SetFanIndex(fan);
                    // Keep diagnostic mode enabled at zero PWM. Mode 0 restores
                    // firmware control; it is not the command to stop a fan.
                    HealthyTable_SetFanTestMode((char)1);
                    int duty = fan == 0 ? cpuDuty : gpuDuty;
                    HealthyTable_SetFanPwmDuty((short)Math.Ceiling(duty * 255.0 / 100.0));
                    rpm[fan] = HealthyTable_FanRPM();
                    if (!ValidRpm(rpm[fan], duty, spinupCycles > 0)) throw new InvalidOperationException("Fan rotation report invalid: restoring automatic control");
                }
                if (spinupCycles > 0) spinupCycles--;
                string state = String.Format(Inv, "{{\"time\":\"{0}\",\"mode\":\"TemperatureControlWorkaround\",\"cpuC\":{1:F1},\"acpiC\":{2:F1},\"gpuC\":{3:F1},\"dutyPercent\":{4},\"cpuRpm\":{5},\"gpuRpm\":{6}}}", DateTimeOffset.Now.ToString("o"), cpu, acpi, gpuTemp, cpuDuty, rpm[0], rpm[1]);
                File.WriteAllText(StatusFile, state);
                if (!File.Exists(LogFile) || new FileInfo(LogFile).Length < 5 * 1024 * 1024) Log(state);
                Beat(workerBeat);
                Thread.Sleep(2000);
            }
            return 0;
        }
        catch (Exception e)
        {
            try { Log("Worker fallback: " + e.Message); File.WriteAllText(StatusFile, "{\"mode\":\"AutomaticFallback\"}"); } catch { }
            return 2;
        }
        finally
        {
            if (initialized) { try { Automatic(); } catch (Exception e) { try { Log("Automatic restore failed: " + e.Message); } catch { } } }
            if (nvml) { try { nvmlShutdown(); } catch { } }
            if (initialized) { try { ShutdownWinIo(); } catch { } }
        }
    }
    static int Guardian()
    {
        bool created;
        using (var singleton = new Mutex(true, @"Global\G14FanGuard", out created))
        {
            if (!created) return 3;
            Directory.CreateDirectory(Data);
            string id = Process.GetCurrentProcess().Id.ToString(Inv);
            string guardBeat = Path.Combine(Data, "guardian-" + id + ".heartbeat");
            string workerBeat = Path.Combine(Data, "worker-" + id + ".heartbeat");
            Process worker = null;
            int result = 2;
            try
            {
                if (File.Exists(StopFile)) return 0;
                Beat(guardBeat);
                var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                    "--worker \"" + guardBeat + "\" \"" + workerBeat + "\"");
                info.UseShellExecute = false; info.CreateNoWindow = true;
                worker = Process.Start(info);
                DateTime started = DateTime.UtcNow;
                DateTime lastLoop = started;
                Log("Guardian started worker " + worker.Id.ToString(Inv));
                while (true)
                {
                    if (DateTime.UtcNow - lastLoop > TimeSpan.FromSeconds(8)) started = DateTime.UtcNow;
                    lastLoop = DateTime.UtcNow;
                    Beat(guardBeat);
                    if (worker.HasExited) { result = worker.ExitCode; break; }
                    if (File.Exists(StopFile)) { result = 0; break; }
                    if (DateTime.UtcNow - started > TimeSpan.FromSeconds(12) && !Fresh(workerBeat))
                        throw new InvalidOperationException("Worker heartbeat expired");
                    Thread.Sleep(1000);
                }
            }
            catch (Exception e) { try { Log("Guardian fallback: " + e.Message); } catch { } }
            finally
            {
                if (worker != null)
                {
                    try { if (!worker.WaitForExit(3000)) { worker.Kill(); worker.WaitForExit(3000); } } catch { }
                    worker.Dispose();
                }
                try { ResetAutomatic(); Log("Guardian restored firmware automatic control"); File.WriteAllText(StatusFile, "{\"mode\":\"AutomaticControlRestored\",\"time\":\"" + DateTimeOffset.Now.ToString("o") + "\"}"); }
                catch (Exception e) { try { Log("Guardian restore failed: " + e.Message); } catch { } }
                try { File.Delete(guardBeat); File.Delete(workerBeat); } catch { }
                try { singleton.ReleaseMutex(); } catch { }
            }
            return result;
        }
    }
    static int SelfTest()
    {
        if (Percent(37) != 0 || Percent(49.99) != 0 || Percent(50) != 15 || Percent(59.99) != 15 || Percent(60) != 20 || Percent(69.99) != 20 || Percent(70) != 25 || Percent(75) != 35 || Percent(80) != 50 || Percent(85) != 75 || Percent(90) != 100 || Percent(100) != 100) return 1;
        if (Ramp(75,25) != 70 || Ramp(25,100) != 100 || Ramp(-1,25) != 50 || Ramp(25,0) != 0 || Ramp(0,25) != 50 || Ramp(-1,0) != 0) return 1;
        if (!ValidRpm(0,0,false) || !ValidRpm(1500,0,false) || !ValidRpm(0,50,true) || ValidRpm(0,50,false) || ValidRpm(-1,0,false) || ValidRpm(15001,0,false)) return 1;
        if (!Hot(90,50,35) || !Hot(40,90,35) || !Hot(40,50,85) || Hot(40,50,35)) return 1;
        for (int t=10;t<=120;t++) if (Percent(t)<0 || Percent(t)>100 || (t>10 && Percent(t)<Percent(t-1)) || (t<50 && Percent(t)!=0) || (t>=50 && Percent(t)<15)) return 1;
        bool rejected = false; try { Percent(Double.NaN); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected) return 1;
        Console.WriteLine("PASS: fan-off threshold, restart boost, stopped/spinning RPM validation, temperature curve, ramping, and invalid-temperature rejection");
        return 0;
    }
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        if (args.Length == 3 && args[0] == "--worker") return Worker(args[1], args[2]);
        if (args.Length == 1 && args[0] == "--restore-auto") { try { ResetAutomatic(); return 0; } catch { return 2; } }
        return Guardian();
    }
}
