// Core Audio interop signatures adapted from EarTrumpet by File-New-Project.
// Source: https://github.com/File-New-Project/EarTrumpet/tree/master/EarTrumpet/Interop
// The EarTrumpet copyright and license are retained in licenses/EarTrumpet.txt.
// The snapshot backend and DTOs below are original application code.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Win10Audio
{
    public sealed class AudioDevice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsDefault { get; set; }
    }

    public sealed class AudioVolume
    {
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
    }

    public sealed class AudioSession
    {
        public int ProcessId { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string ProcessPath { get; set; }
        public string IconPath { get; set; }
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public bool IsSystem { get; set; }
        public bool IsActive { get; set; }
        public int SessionCount { get; set; }
    }

    // No COM object is retained between operations. Call from one UI thread, or
    // initialize COM on a background thread before invoking these methods.
    public static class AudioBackend
    {
        private static readonly Guid EventContext = new Guid("177453C9-9D97-4B82-9376-0E277792C06D");
        private static readonly object SyncRoot = new object();

        public static List<AudioDevice> GetDevices()
        {
            lock (SyncRoot)
            {
                IMMDeviceEnumerator enumerator = null;
                IMMDeviceCollection collection = null;
                IMMDevice defaultDevice = null;
                List<AudioDevice> result = new List<AudioDevice>();
                try
                {
                    enumerator = CreateEnumerator();
                    string defaultId = null;
                    if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice) >= 0 && defaultDevice != null)
                        Check(defaultDevice.GetId(out defaultId));
                    Check(enumerator.EnumAudioEndpoints(0, 1, out collection));
                    uint count;
                    Check(collection.GetCount(out count));
                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice device = null;
                        try
                        {
                            Check(collection.Item(i, out device));
                            string id;
                            Check(device.GetId(out id));
                            result.Add(new AudioDevice { Id = id, Name = ReadDeviceName(device), IsDefault = String.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase) });
                        }
                        catch (COMException) { /* The endpoint may have just been unplugged. */ }
                        finally { Release(device); }
                    }
                    result.Sort(delegate(AudioDevice a, AudioDevice b)
                    {
                        int selected = b.IsDefault.CompareTo(a.IsDefault);
                        return selected != 0 ? selected : String.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                    });
                    return result;
                }
                finally { Release(defaultDevice); Release(collection); Release(enumerator); }
            }
        }

        public static AudioVolume GetMaster()
        {
            lock (SyncRoot)
            {
                return WithMaster(delegate(IAudioEndpointVolume master)
                {
                    float volume;
                    int muted;
                    Check(master.GetMasterVolumeLevelScalar(out volume));
                    Check(master.GetMute(out muted));
                    return new AudioVolume { Volume = volume, IsMuted = muted != 0 };
                });
            }
        }

        public static void SetMasterVolume(float volume)
        { SetMasterVolume(volume, null); }

        public static void SetMasterVolume(float volume, string expectedDeviceId)
        {
            ValidateVolume(volume);
            lock (SyncRoot)
            {
                WithMaster(delegate(IAudioEndpointVolume master)
                {
                    Guid context = EventContext;
                    Check(master.SetMasterVolumeLevelScalar(volume, ref context));
                    return 0;
                }, expectedDeviceId);
            }
        }

        public static void SetMasterMute(bool muted)
        { SetMasterMute(muted, null); }

        public static void SetMasterMute(bool muted, string expectedDeviceId)
        {
            lock (SyncRoot)
            {
                WithMaster(delegate(IAudioEndpointVolume master)
                {
                    Guid context = EventContext;
                    Check(master.SetMute(muted ? 1 : 0, ref context));
                    return 0;
                }, expectedDeviceId);
            }
        }

        public static void SetDefaultDevice(string deviceId)
        {
            if (String.IsNullOrEmpty(deviceId)) throw new ArgumentException("Escolha um dispositivo de saída.", "deviceId");
            lock (SyncRoot)
            {
                bool found = false;
                foreach (AudioDevice device in GetDevices()) if (String.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase)) found = true;
                if (!found) throw new InvalidOperationException("Esse dispositivo foi desconectado. Abra o menu novamente.");
                IMMDeviceEnumerator enumerator = null;
                IPolicyConfig policy = null;
                string[] previous = new string[3];
                int rolesChanged = 0;
                try
                {
                    enumerator = CreateEnumerator();
                    for (int role = 0; role < 3; role++)
                    {
                        IMMDevice previousDevice = null;
                        try
                        {
                            if (enumerator.GetDefaultAudioEndpoint(0, role, out previousDevice) >= 0 && previousDevice != null)
                                Check(previousDevice.GetId(out previous[role]));
                        }
                        finally { Release(previousDevice); }
                    }
                    policy = (IPolicyConfig)new PolicyConfigClient();
                    for (int role = 0; role < 3; role++)
                    {
                        Check(policy.SetDefaultEndpoint(deviceId, role));
                        rolesChanged++;
                    }
                }
                catch
                {
                    // A device can disappear while switching. Restore roles already
                    // changed where the previous device is still available.
                    if (policy != null)
                        for (int role = 0; role < rolesChanged; role++)
                            if (!String.IsNullOrEmpty(previous[role]))
                                try { policy.SetDefaultEndpoint(previous[role], role); } catch (COMException) { }
                    throw;
                }
                finally { Release(policy); Release(enumerator); }
            }
        }

        public static List<AudioSession> GetSessions()
        {
            lock (SyncRoot)
            {
                Dictionary<string, AudioSession> grouped = new Dictionary<string, AudioSession>(StringComparer.Ordinal);
                VisitSessions(delegate(IAudioSessionControl2 control, ISimpleAudioVolume simple)
                {
                    int state;
                    Check(control.GetState(out state));
                    if (state == 2) return; // Expired. Inactive sessions remain visible.
                    uint processId;
                    Check(control.GetProcessId(out processId));
                    string id;
                    Check(control.GetSessionInstanceIdentifier(out id));
                    bool system = control.IsSystemSoundsSession() == 0;
                    string key = system ? "system" : (processId != 0 ? "pid:" + processId : "session:" + id);
                    float volume;
                    int muted;
                    Check(simple.GetMasterVolume(out volume));
                    Check(simple.GetMute(out muted));
                    AudioSession session;
                    if (grouped.TryGetValue(key, out session))
                    {
                        session.Volume = Math.Max(session.Volume, volume);
                        session.IsMuted = session.IsMuted && muted != 0;
                        session.IsActive = session.IsActive || state == 1;
                        session.SessionCount++;
                        return;
                    }
                    string displayName = null;
                    string iconPath = null;
                    control.GetDisplayName(out displayName);
                    control.GetIconPath(out iconPath);
                    string processPath;
                    string name = GetProcessName((int)processId, displayName, out processPath);
                    if (system) name = "Sons do sistema";
                    grouped.Add(key, new AudioSession
                    {
                        ProcessId = (int)processId, Id = id, Name = name,
                        ProcessPath = processPath, IconPath = iconPath,
                        Volume = volume, IsMuted = muted != 0, IsSystem = system,
                        IsActive = state == 1, SessionCount = 1
                    });
                });
                List<AudioSession> result = new List<AudioSession>(grouped.Values);
                result.Sort(delegate(AudioSession a, AudioSession b)
                {
                    int system = b.IsSystem.CompareTo(a.IsSystem);
                    return system != 0 ? system : String.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
                return result;
            }
        }

        public static void SetSessionVolume(int processId, string id, float volume)
        { SetSessionVolume(processId, id, volume, null); }

        public static void SetSessionVolume(int processId, string id, float volume, string expectedDeviceId)
        {
            ValidateVolume(volume);
            SetSessions(processId, id, delegate(ISimpleAudioVolume simple)
            {
                Guid context = EventContext;
                Check(simple.SetMasterVolume(volume, ref context));
            }, expectedDeviceId);
        }

        public static void SetSessionMute(int processId, string id, bool muted)
        { SetSessionMute(processId, id, muted, null); }

        public static void SetSessionMute(int processId, string id, bool muted, string expectedDeviceId)
        {
            SetSessions(processId, id, delegate(ISimpleAudioVolume simple)
            {
                Guid context = EventContext;
                Check(simple.SetMute(muted ? 1 : 0, ref context));
            }, expectedDeviceId);
        }

        private static void SetSessions(int processId, string id, Action<ISimpleAudioVolume> change, string expectedDeviceId)
        {
            lock (SyncRoot)
            {
                int changed = 0;
                COMException failure = null;
                VisitSessions(delegate(IAudioSessionControl2 control, ISimpleAudioVolume simple)
                {
                    uint currentPid;
                    Check(control.GetProcessId(out currentPid));
                    bool matches = processId > 0 && currentPid == processId;
                    if (processId == 0)
                    {
                        string currentId;
                        Check(control.GetSessionInstanceIdentifier(out currentId));
                        matches = String.Equals(currentId, id, StringComparison.Ordinal);
                    }
                    if (!matches) return;
                    try { change(simple); changed++; }
                    catch (COMException error) { failure = error; }
                }, expectedDeviceId);
                if (changed == 0)
                {
                    if (failure != null) throw failure;
                    throw new InvalidOperationException("O aplicativo encerrou sua sessão de áudio. Abra o menu novamente.");
                }
            }
        }

        private static void VisitSessions(Action<IAudioSessionControl2, ISimpleAudioVolume> action, string expectedDeviceId = null)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object managerObject = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                enumerator = CreateEnumerator();
                int defaultResult = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                if (defaultResult == unchecked((int)0x80070490)) return; // No playback device.
                Check(defaultResult);
                VerifyDevice(device, expectedDeviceId);
                Guid managerId = typeof(IAudioSessionManager2).GUID;
                Check(device.Activate(ref managerId, 23, IntPtr.Zero, out managerObject));
                IAudioSessionManager2 manager = (IAudioSessionManager2)managerObject;
                Check(manager.GetSessionEnumerator(out sessions));
                int count;
                Check(sessions.GetCount(out count));
                for (int i = 0; i < count; i++)
                {
                    object sessionObject = null;
                    try
                    {
                        Check(sessions.GetSession(i, out sessionObject));
                        // These are interfaces on one RCW. Release it once only.
                        action((IAudioSessionControl2)sessionObject, (ISimpleAudioVolume)sessionObject);
                    }
                    catch (COMException) { /* A session can disappear mid-enumeration. */ }
                    finally { Release(sessionObject); }
                }
            }
            finally { Release(sessions); Release(managerObject); Release(device); Release(enumerator); }
        }

        private static T WithMaster<T>(Func<IAudioEndpointVolume, T> action, string expectedDeviceId = null)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object masterObject = null;
            try
            {
                enumerator = CreateEnumerator();
                int result = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                if (result == unchecked((int)0x80070490)) throw new InvalidOperationException("Nenhum dispositivo de saída conectado.");
                Check(result);
                VerifyDevice(device, expectedDeviceId);
                Guid masterId = typeof(IAudioEndpointVolume).GUID;
                Check(device.Activate(ref masterId, 23, IntPtr.Zero, out masterObject));
                return action((IAudioEndpointVolume)masterObject);
            }
            finally { Release(masterObject); Release(device); Release(enumerator); }
        }

        private static void VerifyDevice(IMMDevice device, string expectedDeviceId)
        {
            if (String.IsNullOrEmpty(expectedDeviceId)) return;
            string currentId;
            Check(device.GetId(out currentId));
            if (!String.Equals(currentId, expectedDeviceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A saída de áudio mudou. Atualize o painel antes de ajustar o volume.");
        }

        private static string ReadDeviceName(IMMDevice device)
        {
            IPropertyStore store = null;
            PropVariant value = new PropVariant();
            try
            {
                Check(device.OpenPropertyStore(0, out store));
                PropertyKey key = new PropertyKey { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 };
                Check(store.GetValue(ref key, out value));
                if (value.Type == 31 && value.Pointer != IntPtr.Zero)
                {
                    string name = Marshal.PtrToStringUni(value.Pointer);
                    if (!String.IsNullOrEmpty(name)) return name;
                }
                return "Dispositivo de áudio";
            }
            finally { PropVariantClear(ref value); Release(store); }
        }

        private static string GetProcessName(int processId, string displayName, out string processPath)
        {
            processPath = null;
            string fallback = "Aplicativo";
            if (processId > 0)
            {
                try
                {
                    using (Process process = Process.GetProcessById(processId))
                    {
                        fallback = process.ProcessName;
                        try
                        {
                            // Query only the executable path; Process.MainModule
                            // enumerates modules and is slow for large applications.
                            processPath = ReadProcessPath(processId);
                            if (!String.IsNullOrEmpty(processPath))
                            {
                                string description = FileVersionInfo.GetVersionInfo(processPath).FileDescription;
                                if (!String.IsNullOrEmpty(description)) fallback = description;
                            }
                        }
                        catch (System.ComponentModel.Win32Exception) { }
                        catch (InvalidOperationException) { }
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (!String.IsNullOrWhiteSpace(displayName) && displayName[0] != '@') return displayName;
            return fallback;
        }

        private static string ReadProcessPath(int processId)
        {
            IntPtr process = OpenProcess(0x1000, false, processId);
            if (process == IntPtr.Zero) return null;
            try
            {
                StringBuilder buffer = new StringBuilder(32768);
                int length = buffer.Capacity;
                return QueryFullProcessImageName(process, 0, buffer, ref length) ? buffer.ToString() : null;
            }
            finally { CloseHandle(process); }
        }

        private static IMMDeviceEnumerator CreateEnumerator() { return (IMMDeviceEnumerator)new MMDeviceEnumerator(); }
        private static void ValidateVolume(float volume)
        {
            if (Single.IsNaN(volume) || Single.IsInfinity(volume) || volume < 0 || volume > 1)
                throw new ArgumentOutOfRangeException("volume", "O volume deve estar entre 0 e 1.");
        }
        private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }
        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        private class PolicyConfigClient { }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid FormatId; public uint PropertyId; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection collection);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }
        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(ref Guid guid, uint flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(ref Guid guid, uint flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
            [PreserveSig] int RegisterSessionNotification(IntPtr callback);
            [PreserveSig] int UnregisterSessionNotification(IntPtr callback);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr callback);
            [PreserveSig] int UnregisterDuckNotification(IntPtr callback);
        }
        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object control);
        }
        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid guid);
            [PreserveSig] int SetGroupingParam(ref Guid guid, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr callback);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr callback);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint processId);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(int optOut);
        }
        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float value, ref Guid context);
            [PreserveSig] int GetMasterVolume(out float value);
            [PreserveSig] int SetMute(int muted, ref Guid context);
            [PreserveSig] int GetMute(out int muted);
        }
        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float value, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float value, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float value);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float value);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float value, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float value, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float value);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float value);
            [PreserveSig] int SetMute(int muted, ref Guid context);
            [PreserveSig] int GetMute(out int muted);
            [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
            [PreserveSig] int VolumeStepUp(ref Guid context);
            [PreserveSig] int VolumeStepDown(ref Guid context);
            [PreserveSig] int QueryHardwareSupport(out uint mask);
            [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
        }
        // Windows 7 and Windows 10 RS1 onward. Retain vtable positions even for
        // unused methods. SetDefaultEndpoint is the only method this app calls.
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int Unused1();
            [PreserveSig] int Unused2();
            [PreserveSig] int Unused3();
            [PreserveSig] int Unused4();
            [PreserveSig] int Unused5();
            [PreserveSig] int Unused6();
            [PreserveSig] int Unused7();
            [PreserveSig] int Unused8();
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, short visible);
        }
    }
}
