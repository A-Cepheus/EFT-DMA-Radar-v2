using eft_dma_radar.Source.Tarkov;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace eft_dma_radar
{
    internal static class Memory
    {
        private static volatile bool _running = false;
        private static volatile bool _restart = false;
        private static volatile bool _ready = false;
        private static Thread _workerThread;
        private static CancellationTokenSource _workerCancellationTokenSource;
        private static Process _process;
        private static ulong _unityBase;
        private static Game _game;
        private static int _ticksCounter = 0;
        private static volatile int _ticks = 0;
        private static readonly Stopwatch _tickSw = new();

        public static Game.GameStatus GameStatus = Game.GameStatus.NotFound;

        #region Getters
        public static int Ticks
        {
            get => _ticks;
        }
        public static bool InGame
        {
            get => _game?.InGame ?? false;
        }
        public static bool Ready
        {
            get => _ready;
        }
        public static bool InHideout
        {
            get => _game?.InHideout ?? false;
        }
        public static bool IsScav
        {
            get => _game?.IsScav ?? false;
        }

        public static bool IsPvEMode
        {
            get => Program.Config.PvEMode;
        }

        public static bool IsOfflinePvE
        {
            get => (Memory.IsPvEMode && !Memory.IsScav && Memory.MapName != "TarkovStreets");
        }

        public static string MapName
        {
            get => _game?.MapName;
        }

        public static string MapNameFormatted
        {
            get
            {
                var name = Memory.MapName;

                return name switch
                {
                    "factory4_day" or "factory4_night" => "Factory",
                    "bigmap" => "Customs",
                    "RezervBase" => "Reserve",
                    "TarkovStreets" => "Streets of Tarkov",
                    "laboratory" => "The Lab",
                    "Sandbox" or "Sandbox_high" => "Ground Zero",
                    _ => name
                };
            }
        }

        public static ReadOnlyDictionary<string, Player> Players
        {
            get => _game?.Players;
        }

        public static LootManager Loot
        {
            get => _game?.Loot;
        }

        public static ReadOnlyCollection<Grenade> Grenades
        {
            get => _game?.Grenades;
        }

        public static bool LoadingLoot
        {
            get => _game?.LoadingLoot ?? false;
        }

        public static ReadOnlyCollection<Exfil> Exfils
        {
            get => _game?.Exfils;
        }

        public static PlayerManager PlayerManager
        {
            get => _game?.PlayerManager;
        }

        public static QuestManager QuestManager
        {
            get => _game?.QuestManager;
        }

        public static CameraManager CameraManager
        {
            get => _game?.CameraManager;
        }

        public static Toolbox Toolbox
        {
            get => _game?.Toolbox;
        }

        public static Chams Chams
        {
            get => _game?.Chams;
        }

        public static ReadOnlyCollection<PlayerCorpse> Corpses
        {
            get => _game?.Corpses;
        }

        public static Player LocalPlayer
        {
            get
            {
                var game = Memory._game;
                if (game?.Players == null)
                {
                    return null;
                }

                return game.Players.FirstOrDefault((KeyValuePair<string, Player> x) => x.Value.Type == PlayerType.LocalPlayer).Value;
            }
        }
        #endregion

        #region Startup
        /// <summary>
        /// Constructor
        /// </summary>
        static Memory()
        {
            try
            {
                Program.Log("Loading memory module...");

                InitiateMemoryWorker();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Memory Init", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(-1);
            }
        }

        private static void InitiateMemoryWorker()
        {
            Program.Log("Starting Memory worker thread...");
            Memory.StartMemoryWorker();
            Program.HideConsole();
            Memory._tickSw.Start();
        }

        /// <summary>
        /// Gets EFT Process ID.
        /// </summary>
        private static bool GetPid()
        {
            try
            {
                _process = Process.GetProcessesByName("EscapeFromTarkov").FirstOrDefault();
                if (_process == null)
                    throw new Exception("Unable to obtain PID. Game is not running.");
                else
                {
                    Program.Log($"EscapeFromTarkov.exe is running at PID {_process.Id}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Program.Log($"ERROR getting PID: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Gets module base entry address for UnityPlayer.dll
        /// </summary>
        private static bool GetModuleBase()
        {
            try
            {
                _unityBase = GetModuleBaseAddress("UnityPlayer.dll");

                if (_unityBase == 0)
                    throw new Exception("Unable to obtain Base Module Address. Game may not be running");
                else
                {
                    Program.Log($"Found UnityPlayer.dll at 0x{_unityBase.ToString("x")}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Program.Log($"ERROR getting module base: {ex}");
                return false;
            }
        }

        private static ulong GetModuleBaseAddress(string moduleName)
        {
            foreach (ProcessModule module in _process.Modules)
            {
                if (module.ModuleName == moduleName)
                {
                    return (ulong)module.BaseAddress;
                }
            }
            return 0;
        }

        /// <summary>
        /// Returns the Module Base Address of mono-2.0-bdwgc.dll
        /// </summary>
        /// <returns>Module Base Address of mono-2.0-bdwgc.dll</returns>
        public static ulong GetMonoModule()
        {
            ulong monoBase = 0;
            try
            {
                monoBase = GetModuleBaseAddress("mono-2.0-bdwgc.dll");

                if (monoBase == 0)
                    throw new Exception("Unable to obtain Module Base Address. Game may not be running");
                else
                {
                    Program.Log($"Found mono-2.0-bdwgc.dll at 0x{monoBase:x}");
                    return monoBase;
                }
            }
            catch (Exception ex)
            {
                Program.Log($"ERROR getting module base: {ex}");
            }
            return monoBase;
        }
        #endregion

        #region MemoryThread
        private static void StartMemoryWorker()
        {
            if (Memory._workerThread is not null && Memory._workerThread.IsAlive)
            {
                return;
            }

            Memory._workerCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = Memory._workerCancellationTokenSource.Token;

            Memory._workerThread = new Thread(() => Memory.MemoryWorkerThread(cancellationToken))
            {
                Priority = ThreadPriority.BelowNormal,
                IsBackground = true
            };
            Memory._running = true;
            Memory._workerThread.Start();
        }

        public static async void StopMemoryWorker()
        {
            await Task.Run(() =>
            {
                if (Memory._workerCancellationTokenSource is not null)
                {
                    Memory._workerCancellationTokenSource.Cancel();
                    Memory._workerCancellationTokenSource.Dispose();
                    Memory._workerCancellationTokenSource = null;
                }

                if (Memory._workerThread is not null)
                {
                    Memory._workerThread.Join();
                    Memory._workerThread = null;
                }
            });
        }

        private static void MemoryWorkerThread(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Memory.MemoryWorker();
                }
                catch { }

            }
            Program.Log("[Memory] Refresh thread stopped.");
        }

        /// <summary>
        /// Main worker to perform memory reads.
        /// </summary>
        private static void MemoryWorker()
        {
            try
            {
                while (true)
                {
                    Program.Log("Attempting to find EFT Process...");
                    while (!Memory.GetPid() || !Memory.GetModuleBase())
                    {
                        Program.Log("EFT startup failed, trying again in 15 seconds...");
                        Memory.GameStatus = Game.GameStatus.NotFound;
                        Thread.Sleep(15000);
                    }
                    Program.Log("EFT process located! Startup successful.");
                    while (true)
                    {
                        Memory._game = new Game(Memory._unityBase);
                        Player.Reset();
                        try
                        {
                            Program.Log("Ready -- Waiting for raid...");
                            Memory.GameStatus = Game.GameStatus.Menu;
                            Memory._ready = true;
                            Memory._game.WaitForGame();
                            while (Memory.GameStatus == Game.GameStatus.InGame)
                            {
                                if (Memory._tickSw.ElapsedMilliseconds >= 1000)
                                {
                                    Memory._ticks = _ticksCounter;
                                    Memory._ticksCounter = 0;
                                    Memory._tickSw.Restart();
                                }
                                else
                                {
                                    Memory._ticksCounter++;
                                }

                                if (Memory._restart)
                                {
                                    Memory.GameStatus = Game.GameStatus.Menu;
                                    Program.Log("Restarting game... getting fresh GameWorld instance");
                                    Memory._restart = false;
                                    if (TimeScaleManager.working)
                                        TimeScaleManager.ResetTimeScale();
                                    break;
                                }
                                Memory._game.GameLoop();
                                Thread.SpinWait(1000);
                            }
                        }
                        catch (GameNotRunningException) { break; }
                        catch (ThreadInterruptedException) { throw; }
                        catch (Exception ex)
                        {
                            Program.Log($"CRITICAL ERROR in Game Loop: {ex}");
                        }
                        finally
                        {
                            Memory._ready = false;
                            Thread.Sleep(100);
                        }
                    }
                    Program.Log("Game is no longer running! Attempting to restart...");
                }
            }
            catch (ThreadInterruptedException) { }
            catch (Exception ex)
            {
                Environment.FailFast($"FATAL ERROR on Memory Thread: {ex}");
            }
        }
        #endregion

        #region ReadMethods
        /// <summary>
        /// Read memory into a Span.
        /// </summary>
        public static Span<byte> ReadBuffer(ulong addr, int size)
        {
            if ((uint)size > PAGE_SIZE * 1500) throw new Exception("Buffer length outside expected bounds!");
            var buf = new byte[size];
            try
            {
                IntPtr bytesRead;
                if (!ReadProcessMemory(_process.Handle, (IntPtr)addr, buf, size, out bytesRead) || bytesRead.ToInt64() != size)
                    throw new Exception("Incomplete memory read!");
            }
            catch (Exception ex)
            {
                throw new Exception($"ERROR reading buffer at 0x{addr.ToString("X")}", ex);
            }
            return buf;
        }

        /// <summary>
        /// Read a chain of pointers and get the final result.
        /// </summary>
        public static ulong ReadPtrChain(ulong ptr, uint[] offsets)
        {
            ulong addr = 0;
            try { addr = ReadPtr(ptr + offsets[0]); }
            catch (Exception ex) { throw new Exception($"ERROR reading pointer chain at index 0, addr 0x{ptr.ToString("X")} + 0x{offsets[0].ToString("X")}", ex); }
            for (int i = 1; i < offsets.Length; i++)
            {
                try { addr = ReadPtr(addr + offsets[i]); }
                catch (Exception ex) { throw new Exception($"ERROR reading pointer chain at index {i}, addr 0x{addr.ToString("X")} + 0x{offsets[i].ToString("X")}", ex); }
            }
            return addr;
        }
        /// <summary>
        /// Resolves a pointer and returns the memory address it points to.
        /// </summary>
        public static ulong ReadPtr(ulong ptr)
        {
            var addr = ReadValue<ulong>(ptr);
            if (addr == 0x0) throw new NullPtrException();
            else return addr;
        }

        /// <summary>
        /// Resolves a pointer and returns the memory address it points to.
        /// </summary>
        public static ulong ReadPtrNullable(ulong ptr)
        {
            var addr = ReadValue<ulong>(ptr);
            return addr;
        }

        /// <summary>
        /// Read value type/struct from specified address.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to read from.</param>
        public static T ReadValue<T>(ulong addr)
            where T : struct
        {
            try
            {
                int size = Marshal.SizeOf(typeof(T));
                var buf = new byte[size];
                IntPtr bytesRead;
                if (!ReadProcessMemory(_process.Handle, (IntPtr)addr, buf, size, out bytesRead) || bytesRead.ToInt64() != size)
                    throw new Exception("Incomplete memory read!");
                return MemoryMarshal.Read<T>(buf);
            }
            catch (Exception ex)
            {
                throw new Exception($"ERROR reading {typeof(T)} value at 0x{addr.ToString("X")}", ex);
            }
        }

        /// <summary>
        /// Read null terminated string.
        /// </summary>
        /// <param name="length">Number of bytes to read.</param>
        /// <exception cref="Exception"></exception>
        public static string ReadString(ulong addr, uint length) // read n bytes (string)
        {
            try
            {
                if (length > PAGE_SIZE) throw new Exception("String length outside expected bounds!");
                var buf = new byte[length];
                IntPtr bytesRead;
                ReadProcessMemory(_process.Handle, (IntPtr)addr, buf, (int)length, out bytesRead); //|| bytesRead.ToInt64() != length)
                    //throw new Exception("Incomplete memory read!");
                return Encoding.Default.GetString(buf).Split('\0')[0];
            }
            catch (Exception ex)
            {
                throw new Exception($"ERROR reading string at 0x{addr.ToString("X")}", ex);
            }
        }

        /// <summary>
        /// Read UnityEngineString structure
        /// </summary>
        public static string ReadUnityString(ulong addr)
        {
            try
            {
                var length = (uint)ReadValue<int>(addr + Offsets.UnityString.Length);
                if (length > PAGE_SIZE) throw new Exception("String length outside expected bounds!");
                var buf = new byte[length * 2];
                IntPtr bytesRead;
                if (!ReadProcessMemory(_process.Handle, (IntPtr)(addr + Offsets.UnityString.Value), buf, (int)(length * 2), out bytesRead) || bytesRead.ToInt64() != length * 2)
                    throw new Exception("Incomplete memory read!");
                return Encoding.Unicode.GetString(buf).TrimEnd('\0');
            }
            catch (Exception ex)
            {
                throw new Exception($"ERROR reading UnityString at 0x{addr.ToString("X")}", ex);
            }
        }
        #endregion

        #region WriteMethods

        /// <summary>
        /// (Base)
        /// Write value type/struct to specified address.
        /// </summary>
        /// <typeparam name="T">Value Type to write.</typeparam>
        /// <param name="addr">Virtual Address to write to.</param>
        /// <param name="value"></param>
        /// <exception cref="Exception"></exception>
        public static void WriteValue<T>(ulong addr, T value)
            where T : unmanaged
        {
            try
            {
                int size = Marshal.SizeOf(typeof(T));
                byte[] buffer = new byte[size];
                MemoryMarshal.Write(buffer, ref value);
                IntPtr bytesWritten;
                if (!WriteProcessMemory(_process.Handle, (IntPtr)addr, buffer, size, out bytesWritten) || bytesWritten.ToInt64() != size)
                    throw new Exception("Memory Write Failed!");
            }
            catch (Exception ex)
            {
                throw new Exception($"ERROR writing {typeof(T)} value at 0x{addr.ToString("X")}", ex);
            }
        }
        #endregion

        #region Methods
        /// <summary>
        /// Sets restart flag to re-initialize the game/pointers from the bottom up.
        /// </summary>
        public static void Restart()
        {
            if (InGame)
                _restart = true;
        }

        /// <summary>
        /// Refresh loot only.
        /// </summary>
        public static void RefreshLoot()
        {
            _game?.RefreshLoot();
        }
        /// <summary>
        /// Close down DMA Device Connection.
        /// </summary>
        public static void Shutdown()
        {
            if (_running)
            {
                Program.Log("Closing down Memory Thread...");
                _running = false;
                Memory.StopMemoryWorker();
            }
        }

        private static void ThrowIfMemoryShutdown()
        {
            if (!_running) throw new Exception("Memory Thread is shutting down!");
        }

        /// Mem Align Functions Ported from Win32 (C Macros)
        private const ulong PAGE_SIZE = 0x1000;
        private const int PAGE_SHIFT = 12;

        /// <summary>
        /// The PAGE_ALIGN macro takes a virtual address and returns a page-aligned
        /// virtual address for that page.
        /// </summary>
        private static ulong PAGE_ALIGN(ulong va)
        {
            return (va & ~(PAGE_SIZE - 1));
        }
        /// <summary>
        /// The ADDRESS_AND_SIZE_TO_SPAN_PAGES macro takes a virtual address and size and returns the number of pages spanned by the size.
        /// </summary>
        private static uint ADDRESS_AND_SIZE_TO_SPAN_PAGES(ulong va, uint size)
        {
            return (uint)((BYTE_OFFSET(va) + (size) + (PAGE_SIZE - 1)) >> PAGE_SHIFT);
        }

        /// <summary>
        /// The BYTE_OFFSET macro takes a virtual address and returns the byte offset
        /// of that address within the page.
        /// </summary>
        private static uint BYTE_OFFSET(ulong va)
        {
            return (uint)(va & (PAGE_SIZE - 1));
        }
        #endregion

        #region Interop
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);
        #endregion
        #region ScatterRead
        /// <summary>
        /// (Base)
        /// Performs multiple reads in one sequence, significantly faster than single reads.
        /// Designed to run without throwing unhandled exceptions, which will ensure the maximum amount of
        /// reads are completed OK even if a couple fail.
        /// </summary>
        /// <param name="entries">Scatter Read Entries to read from for this round.</param>
        internal static void ReadScatter(ReadOnlySpan<IScatterEntry> entries)
        {
            foreach (var entry in entries) // First loop through all entries - GET INFO
            {
                if (entry is null)
                    continue;

                ulong addr = entry.ParseAddr();
                uint size = (uint)entry.ParseSize();

                // INTEGRITY CHECK - Make sure the read is valid
                if (addr == 0x0 || size == 0)
                {
                    entry.IsFailed = true;
                    continue;
                }

                ulong readAddress = addr + entry.Offset;

                try
                {
                    var buffer = new byte[size];
                    IntPtr bytesRead;
                    if (ReadProcessMemory(_process.Handle, (IntPtr)readAddress, buffer, (int)size, out bytesRead) && bytesRead.ToInt64() == size)
                    {
                        entry.SetResult(buffer);
                    }
                    else
                    {
                        entry.IsFailed = true;
                    }
                }
                catch
                {
                    entry.IsFailed = true;
                }
            }
        }
        #endregion

        #region WriteScatter
        /// <summary>
        /// Performs multiple memory write operations in a single call
        /// </summary>
        /// <param name="entries">A collection of entries defining the memory writes.</param>
        public static void WriteScatter(IEnumerable<IScatterWriteEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (entry is null)
                    continue;

                try
                {
                    byte[] buffer;
                    int size;

                    switch (entry)
                    {
                        case IScatterWriteDataEntry<int> intEntry:
                            buffer = BitConverter.GetBytes(intEntry.Data);
                            size = buffer.Length;
                            break;
                        case IScatterWriteDataEntry<float> floatEntry:
                            buffer = BitConverter.GetBytes(floatEntry.Data);
                            size = buffer.Length;
                            break;
                        case IScatterWriteDataEntry<ulong> ulongEntry:
                            buffer = BitConverter.GetBytes(ulongEntry.Data);
                            size = buffer.Length;
                            break;
                        case IScatterWriteDataEntry<bool> boolEntry:
                            buffer = BitConverter.GetBytes(boolEntry.Data);
                            size = buffer.Length;
                            break;
                        case IScatterWriteDataEntry<byte> byteEntry:
                            buffer = new byte[] { byteEntry.Data };
                            size = buffer.Length;
                            break;
                        default:
                            throw new NotSupportedException($"Unsupported data type: {entry.GetType()}");
                    }

                    IntPtr bytesWritten;
                    if (!WriteProcessMemory(_process.Handle, (IntPtr)entry.Address, buffer, size, out bytesWritten) || bytesWritten.ToInt64() != size)
                    {
                        Program.Log($"Failed to write memory at address: {entry.Address}");
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"Exception during WriteScatter: {ex}");
                }
            }
        }
        #endregion
    }

    #region Exceptions
    public class NullPtrException : Exception
    {
        public NullPtrException()
        {
        }

        public NullPtrException(string message)
            : base(message)
        {
        }

        public NullPtrException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
    #endregion
}
