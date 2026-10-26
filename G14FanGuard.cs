using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

/*
ASUS diagnostic API declarations adapted from AsusFanControl.
Copyright (c) 2023 Karmel0x

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
internal static class G14FanGuard
{
    static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "G14FanGuard");
    static readonly string StopFile = Path.Combine(Data, "stop.request");
    static readonly string StatusFile = Path.Combine(Data, "status.json");
    static readonly string LogFile = Path.Combine(Data, "controller.log");
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate uint PowerCallback(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)]
    struct PowerSubscription { internal IntPtr Callback; internal IntPtr Context; }
    [DllImport("powrprof.dll")] static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr handle);
    [DllImport("powrprof.dll")] static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);
    sealed class PowerMonitor : IDisposable
    {
        readonly AutoResetEvent changed = new AutoResetEvent(false);
        readonly ManualResetEvent cleaned = new ManualResetEvent(false);
        readonly bool waitForCleanup;
        readonly Func<DateTime> now;
        readonly Action<string> logger;
        readonly PowerCallback callback;
        IntPtr subscription, registration;
        volatile bool suspended;
        long readyTicks;
        internal bool Suspended { get { return suspended; } }
        internal bool Ready { get { return !suspended && now().Ticks >= Interlocked.Read(ref readyTicks); } }
        internal PowerMonitor(bool cleanup, bool register = true, Func<DateTime> clock = null, Action<string> log = null)
        {
            waitForCleanup = cleanup; now = clock ?? (() => DateTime.UtcNow); logger = log ?? Log;
            callback = delegate(IntPtr context, uint type, IntPtr setting) {
                try { Notify(type); } catch { }
                return 0;
            };
            if (!register) return;
            try
            {
                subscription = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PowerSubscription)));
                Marshal.StructureToPtr(new PowerSubscription { Callback = Marshal.GetFunctionPointerForDelegate(callback), Context = IntPtr.Zero }, subscription, false);
                uint error = PowerRegisterSuspendResumeNotification(2,subscription,out registration);
                if (error != 0) throw new System.ComponentModel.Win32Exception((int)error,"Power notification registration failed");
                logger("Suspend/resume notifications registered");
            }
            catch { Dispose(); throw; }
        }
        internal void Notify(uint type)
        {
            if (type == 4)
            {
                cleaned.Reset(); suspended = true; changed.Set();
                logger("Power suspend notification");
                if (waitForCleanup) logger(cleaned.WaitOne(1500) ? "Suspend cleanup completed" : "Suspend cleanup deadline reached");
            }
            else if ((type == 7 || type == 18) && suspended)
            {
                Interlocked.Exchange(ref readyTicks,now().AddSeconds(3).Ticks);
                suspended = false; changed.Set(); logger("Power resume notification");
            }
        }
        internal void Wait(int milliseconds) { changed.WaitOne(milliseconds); }
        internal void Complete() { cleaned.Set(); }
        public void Dispose()
        {
            cleaned.Set();
            if (registration != IntPtr.Zero) { PowerUnregisterSuspendResumeNotification(registration); registration = IntPtr.Zero; }
            if (subscription != IntPtr.Zero) { Marshal.FreeHGlobal(subscription); subscription = IntPtr.Zero; }
            GC.KeepAlive(callback);
            changed.Dispose(); cleaned.Dispose();
        }
    }
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
        if (c < 50) return 0;
        double[] temperatures = { 50, 60, 65, 70, 75, 80, 85, 90 };
        int[] duties = { 15, 16, 18, 21, 25, 32, 42, 50 };
        for (int i = 1; i < temperatures.Length; i++)
            if (c < temperatures[i])
                return (int)Math.Round(duties[i-1] + (c-temperatures[i-1]) * (duties[i]-duties[i-1]) / (temperatures[i]-temperatures[i-1]));
        return 50;
    }
    static int Ramp(int previous, int target)
    {
        if (target == 0) return 0;
        if (previous <= 0) return target >= 30 ? target : 15;
        return target > previous ? Math.Min(target, previous + 1) : Math.Max(target, previous - 1);
    }
    sealed class ControlState
    {
        internal int Duty;
        internal int SpinupCycles;
        bool running;
        double filtered = Double.NaN;
        internal int Update(double temperature)
        {
            Percent(temperature);
            filtered = Double.IsNaN(filtered) ? temperature : filtered + 0.2 * (temperature-filtered);
            if (temperature < 50 || (!running && temperature < 55))
            {
                running = false; Duty = 0; SpinupCycles = 0; filtered = temperature;
                return Duty;
            }
            int target = Percent(temperature >= 80 ? temperature : filtered);
            if (!running)
            {
                running = true; SpinupCycles = 6; Duty = Ramp(0,target);
            }
            else if (SpinupCycles > 0)
                Duty = Math.Min(Math.Max(25,target),Duty+2);
            else if (temperature >= 80 && target > Duty)
                Duty = Math.Min(target,Duty+3);
            else Duty = Ramp(Duty,target);
            return Duty;
        }
        internal void Observe(int cpuRpm, int gpuRpm)
        {
            if (!ValidRpm(cpuRpm,Duty,SpinupCycles>0) || !ValidRpm(gpuRpm,Duty,SpinupCycles>0))
                throw new InvalidOperationException("Invalid fan rotation");
            if (Duty == 0) return;
            if (cpuRpm >= 500 && gpuRpm >= 500) SpinupCycles = 0;
            else if (--SpinupCycles <= 0) throw new InvalidOperationException("Fan spin-up failed");
        }
    }
    static bool ValidRpm(int rpm, int duty, bool starting)
    {
        // Zero is valid while stopped and during spin-up.
        return rpm >= 0 && rpm <= 15000 && (duty == 0 || starting || rpm >= 500);
    }
    static bool Hot(double cpu, double acpi, double gpu) { return cpu >= 90 || acpi >= 90 || gpu >= 85; }
    static void Log(string message)
    {
        try
        {
            using (var stream = new FileStream(LogFile,FileMode.Append,FileAccess.Write,FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream))
                writer.WriteLine(DateTimeOffset.Now.ToString("o") + " " + message);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    static void Beat(string path) { File.WriteAllText(path, DateTime.UtcNow.ToString("o")); }
    static bool Fresh(string path) { return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(10); }
    static bool GuardianFresh(string path, int processId)
    {
        if (!Fresh(path)) return false;
        try { using (var parent = Process.GetProcessById(processId)) return !parent.HasExited; }
        catch (ArgumentException) { return false; }
    }
    static void Status(string mode, string detail)
    {
        string escaped = detail.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r"," ").Replace("\n"," ");
        File.WriteAllText(StatusFile,"{\"time\":\""+DateTimeOffset.Now.ToString("o")+"\",\"mode\":\""+mode+"\",\"detail\":\""+escaped+"\"}");
    }
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
    static int Worker(string guardianBeat, string workerBeat, int guardianId)
    {
        bool initialized = false, nvml = false;
        PowerMonitor power = null;
        try
        {
            power = new PowerMonitor(true);
            if (!GuardianFresh(guardianBeat,guardianId)) throw new InvalidOperationException("Guardian unavailable");
            if (!power.Ready) return 5;
            Beat(workerBeat);
            InitializeWinIo(); initialized = true;
            if (HealthyTable_FanCounts() != 2) throw new InvalidOperationException("Expected two ASUS diagnostic fans");
            if (nvmlInit_v2() != 0) throw new InvalidOperationException("NVIDIA temperature interface unavailable");
            nvml = true;
            IntPtr gpu;
            if (nvmlDeviceGetHandleByIndex_v2(0, out gpu) != 0) throw new InvalidOperationException("NVIDIA GPU unavailable");
            var control = new ControlState();
            DateTime lastLoop = DateTime.UtcNow;
            while (!File.Exists(StopFile))
            {
                if (!power.Ready) return 5;
                if (DateTime.UtcNow - lastLoop > TimeSpan.FromSeconds(8))
                {
                    Log("Sensor session reset after suspend or delayed polling");
                    return 4;
                }
                lastLoop = DateTime.UtcNow;
                if (!GuardianFresh(guardianBeat,guardianId)) throw new InvalidOperationException("Guardian unavailable");
                double cpu = Thermal_Read_Cpu_Temperature();
                double acpi = AcpiTemperature();
                uint gpuRaw;
                if (nvmlDeviceGetTemperature(gpu, 0, out gpuRaw) != 0) throw new InvalidOperationException("NVIDIA temperature read failed");
                double gpuTemp = gpuRaw;
                if (cpu < 10 || cpu > 120 || gpuTemp < 10 || gpuTemp > 120) throw new InvalidOperationException(String.Format(Inv,"Invalid sensor: CPU {0:F1} C, GPU {1:F1} C",cpu,gpuTemp));
                if (Hot(cpu, acpi, gpuTemp))
                {
                    Automatic(); control = new ControlState();
                    string hotState = String.Format(Inv,"{{\"time\":\"{0}\",\"mode\":\"FirmwareCoolingHighTemperature\",\"cpuC\":{1:F1},\"acpiC\":{2:F1},\"gpuC\":{3:F1}}}",DateTimeOffset.Now.ToString("o"),cpu,acpi,gpuTemp);
                    File.WriteAllText(StatusFile, hotState);
                    Beat(workerBeat);
                    power.Wait(2000);
                    continue;
                }
                double controlTemp = Math.Max(Math.Max(cpu, acpi), gpuTemp);
                // Both fans use the hottest observed temperature because they share cooling pipes.
                int duty = control.Update(controlTemp);
                int[] rpm = new int[2];
                for (byte fan = 0; fan < 2; fan++)
                {
                    HealthyTable_SetFanIndex(fan);
                    // Keep diagnostic mode enabled at zero PWM. Mode 0 restores
                    // firmware control; it is not the command to stop a fan.
                    HealthyTable_SetFanTestMode((char)1);
                    HealthyTable_SetFanPwmDuty((short)Math.Ceiling(duty * 255.0 / 100.0));
                    rpm[fan] = HealthyTable_FanRPM();
                }
                control.Observe(rpm[0],rpm[1]);
                string state = String.Format(Inv, "{{\"time\":\"{0}\",\"mode\":\"TemperatureControlWorkaround\",\"cpuC\":{1:F1},\"acpiC\":{2:F1},\"gpuC\":{3:F1},\"dutyPercent\":{4},\"cpuRpm\":{5},\"gpuRpm\":{6}}}", DateTimeOffset.Now.ToString("o"), cpu, acpi, gpuTemp, duty, rpm[0], rpm[1]);
                File.WriteAllText(StatusFile, state);
                if (!File.Exists(LogFile) || new FileInfo(LogFile).Length < 5 * 1024 * 1024) Log(state);
                Beat(workerBeat);
                power.Wait(2000);
            }
            return 0;
        }
        catch (Exception e)
        {
            try { Log("Worker fallback: " + e.Message); Status("AutomaticFallback",e.Message); } catch { }
            return 2;
        }
        finally
        {
            if (initialized) { try { Automatic(); } catch (Exception e) { try { Log("Automatic restore failed: " + e.Message); } catch { } } }
            if (nvml) { try { nvmlShutdown(); } catch { } }
            if (initialized) { try { ShutdownWinIo(); } catch { } }
            if (power != null) { power.Complete(); power.Dispose(); }
        }
    }
    static void Supervise(Func<Process> launch, Func<bool> stop, Func<bool> fresh, Action beat,
        Func<bool> restore, Action<string> log, Action<string,string> status, int retryMilliseconds, Func<bool> ready = null)
    {
        while (!stop())
        {
            while (ready != null && !ready() && !stop()) { beat(); Thread.Sleep(100); }
            if (stop()) break;
            Process worker = null;
            string failure = "Worker stopped";
            beat();
            try
            {
                worker = launch();
                DateTime started = DateTime.UtcNow;
                DateTime lastLoop = started;
                log("Guardian started worker " + worker.Id.ToString(Inv));
                while (!stop())
                {
                    if (ready != null && !ready()) { failure = "Power transition"; break; }
                    if (DateTime.UtcNow - lastLoop > TimeSpan.FromSeconds(8)) started = DateTime.UtcNow;
                    lastLoop = DateTime.UtcNow;
                    beat();
                    if (worker.HasExited) { failure = "Worker exit " + worker.ExitCode.ToString(Inv); break; }
                    if (DateTime.UtcNow - started > TimeSpan.FromSeconds(12) && !fresh())
                        throw new InvalidOperationException("Worker heartbeat expired");
                    Thread.Sleep(1000);
                }
            }
            catch (Exception e) { failure = e.Message; }
            finally
            {
                if (worker != null)
                {
                    try
                    {
                        if (!worker.WaitForExit(3000)) { worker.Kill(); worker.WaitForExit(3000); }
                        if (!worker.HasExited) throw new InvalidOperationException("Worker could not be terminated");
                    }
                    finally { worker.Dispose(); }
                }
                try { if (restore()) log("Guardian restored firmware automatic control"); else log("Automatic restore deferred during power transition"); }
                catch (Exception e) { log("Guardian restore failed: " + e.Message); }
            }
            if (stop()) break;
            log("Recovery: " + failure + "; retry pending");
            status("Recovering",failure);
            DateTime retryAt = DateTime.UtcNow.AddMilliseconds(retryMilliseconds);
            while (DateTime.UtcNow < retryAt && !stop())
            {
                beat();
                Thread.Sleep(Math.Min(1000,retryMilliseconds));
            }
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
            using (var power = new PowerMonitor(false))
            {
            try
            {
                Supervise(delegate {
                    var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                        "--worker \"" + guardBeat + "\" \"" + workerBeat + "\" " + id);
                    info.UseShellExecute = false; info.CreateNoWindow = true;
                    return Process.Start(info);
                }, () => File.Exists(StopFile), () => Fresh(workerBeat), () => Beat(guardBeat),
                delegate { if (!power.Ready) return false; ResetAutomatic(); return true; },Log,Status,2000,() => power.Ready);
            }
            finally
            {
                try { if (power.Ready) ResetAutomatic(); Status("AutomaticControlRestored","Controller stopped"); }
                catch (Exception e) { try { Log("Guardian restore failed: " + e.Message); } catch { } }
                try { File.Delete(guardBeat); File.Delete(workerBeat); } catch { }
                try { singleton.ReleaseMutex(); } catch { }
            }
            return 0;
            }
        }
    }
    static bool RecoveryTest()
    {
        int launches = 0, restores = 0, failures = 0;
        var deadline = Stopwatch.StartNew();
        int readinessChecks = 0;
        Supervise(delegate {
            launches++;
            var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--self-test-child");
            info.UseShellExecute = false; info.CreateNoWindow = true;
            return Process.Start(info);
        }, () => launches >= 7 || deadline.ElapsedMilliseconds > 20000, () => true, () => {}, () => { restores++; return true; },
        message => {}, (mode,detail) => { if (mode == "Recovering") failures++; },10,() => ++readinessChecks > 3);
        return launches == 7 && restores == 7 && failures == 6 && readinessChecks > 3;
    }
    static int SelfTest()
    {
        if (Percent(37) != 0 || Percent(49.99) != 0 || Percent(50) != 15 || Percent(60) != 16 || Percent(65) != 18 || Percent(70) != 21 || Percent(75) != 25 || Percent(80) != 32 || Percent(85) != 42 || Percent(90) != 50 || Percent(100) != 50) return 1;
        if (Ramp(25,15) != 24 || Ramp(15,25) != 16 || Ramp(-1,25) != 15 || Ramp(25,0) != 0 || Ramp(0,15) != 15 || Ramp(-1,0) != 0) return 1;
        if (!ValidRpm(0,0,false) || !ValidRpm(1500,0,false) || !ValidRpm(0,50,true) || ValidRpm(0,50,false) || ValidRpm(-1,0,false) || ValidRpm(15001,0,false)) return 1;
        if (!Hot(90,50,35) || !Hot(40,90,35) || !Hot(40,50,85) || Hot(40,50,35)) return 1;
        for (int t=10;t<=120;t++) if (Percent(t)<0 || Percent(t)>50 || (t>10 && Percent(t)<Percent(t-1)) || (t<50 && Percent(t)!=0) || (t>=50 && Percent(t)<15)) return 1;
        var control = new ControlState();
        if (control.Update(49) != 0 || control.Update(54.9) != 0 || control.Update(55) != 15) return 1;
        control.Observe(1800,1700);
        for (int i=0;i<40;i++)
        {
            int previous = control.Duty;
            int duty = control.Update(74);
            if (Math.Abs(duty-previous)>1 || duty<15 || duty>25) return 1;
            control.Observe(2000,1900);
        }
        if (control.Update(49) != 0 || control.Update(52) != 0) return 1;
        var start = new ControlState();
        start.Update(55);
        start.Observe(0,0);
        if (start.Update(55) != 17) return 1;
        start.Observe(1000,1000);
        if (start.Update(55) != 16) return 1;
        bool stall = false;
        try { var stuck = new ControlState(); for (int i=0;i<6;i++) { stuck.Update(55); stuck.Observe(0,0); } }
        catch (InvalidOperationException) { stall = true; }
        if (!stall) return 1;
        bool rejected = false; try { Percent(Double.NaN); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected) return 1;
        if (!RecoveryTest()) return 1;
        DateTime clock = DateTime.UtcNow;
        using (var power = new PowerMonitor(false,false,() => clock,message => {}))
        {
            if (!power.Ready) return 1;
            power.Notify(4);
            if (!power.Suspended || power.Ready) return 1;
            power.Notify(18);
            if (power.Suspended || power.Ready) return 1;
            clock = clock.AddSeconds(3.1);
            if (!power.Ready) return 1;
            power.Notify(7);
            if (!power.Ready) return 1;
        }
        using (var power = new PowerMonitor(false,true,null,message => {})) { if (!power.Ready) return 1; }
        bool cleanup = false;
        using (var power = new PowerMonitor(true,false,null,message => {}))
        {
            var worker = new Thread(() => { power.Wait(2000); if (power.Suspended) { cleanup = true; power.Complete(); } });
            worker.Start(); power.Notify(4);
            if (!worker.Join(2000) || !cleanup) return 1;
        }
        Console.WriteLine("PASS: curve, smoothing, hysteresis, spin-up, sensors, repeated recovery, suspend cleanup, resume delay, and power notification registration");
        return 0;
    }
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        if (args.Length == 1 && args[0] == "--self-test-child") return 2;
        int guardianId;
        if (args.Length == 4 && args[0] == "--worker" && Int32.TryParse(args[3],out guardianId) && guardianId > 0) return Worker(args[1], args[2],guardianId);
        if (args.Length == 1 && args[0] == "--restore-auto") { try { ResetAutomatic(); return 0; } catch { return 2; } }
        return args.Length == 0 ? Guardian() : 1;
    }
}
