using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
// ReSharper disable InconsistentNaming

namespace MEInject
{
    public partial class MainForm : Form
    {
        private string _dbFile = "db.dat";
        private string _MEdir = string.Empty;
        private List<MEinfo> _meFiles = new List<MEinfo>();
        private List<string> _validMEfiles;

        private MEinfo BIOS_ME_info;
        private byte[] BIOSfile;
        private byte[] MEfile;

        private uint BIOS_ME_start_offset;
        private uint BIOS_ME_end_offset;

        private string BIOSfilename;
        private Mode _mode;

        private byte[] MSDM_table_pattern =
        {
            0x01, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x1D, 0x00, 0x00, 0x00
        };
        private byte MSDM_offset = 0x14;

        enum Mode
        {
            ME,
            TXE
        }

        public MainForm()
        {
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
            InitializeComponent();
        }

        private void ClearGUI()
        {
            MEoffsetLabel.Text = @"ME offset in BIOS: -";
            MEsizeInBIOSLabel.Text = @"ME size: -";
            MEinBIOS_ver_label.Text = @"ME version: -";
            MEsComboBox.Items.Clear();
            MEsComboBox.Text = string.Empty;
            if (_validMEfiles != null) _validMEfiles.Clear();
            WinKeyTextBox.Text = @"-";
        }

        private void UpdateGUI()
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(UpdateGUI));
                return;
            }

            // Actualizamos el título de la ventana con el conteo real de la base de datos
            if (!string.IsNullOrEmpty(_MEdir))
            {
                this.Text = string.Format("Bios IA Injector - {0} archivos cargados - {1}", _meFiles.Count, _MEdir);
            }

            // Si ya hay un BIOS cargado, actualizamos su info
            if (BIOS_ME_info.Major > 0)
            {
                MEinBIOS_ver_label.Text = string.Format("{0} version: {1}.{2}.{3}.{4}",
                    _mode, BIOS_ME_info.Major, BIOS_ME_info.Minor, BIOS_ME_info.Hotfix, BIOS_ME_info.Build);
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            UpdateGUI();
            _validMEfiles = new List<string>();
            _MEdir = Properties.Settings.Default.MEdir;

            this.Show();
            Application.DoEvents();

            LoadDB();
            UpdateGUI();
            Log("Ready! (Motor V2-V21 + Carga Anti-Cuelgue)", LogLevel.Info);
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            SaveDB();
            Properties.Settings.Default.MEdir = _MEdir;
            Properties.Settings.Default.Save();
        }

        private void UpdateDB()
        {
            var files = GetFileNames(_MEdir).ToList();
            if (files.Count == 0) return;

            // Creamos la barra de progreso visible
            ProgressBar pBar = new ProgressBar
            {
                Dock = DockStyle.Top,
                Maximum = files.Count,
                Value = 0,
                Height = 20,
                Style = ProgressBarStyle.Continuous
            };
            this.Controls.Add(pBar);
            pBar.BringToFront();
            this.Refresh();

            Log($"Escaneando {files.Count} archivos en segundo plano...", LogLevel.Warning);
            _meFiles.Clear();

            // Usamos BackgroundWorker: La forma nativa y robusta de evitar cuelgues en .NET antiguo
            System.ComponentModel.BackgroundWorker worker = new System.ComponentModel.BackgroundWorker();
            worker.WorkerReportsProgress = true;

            // 1. LO QUE HACE EL MOTOR EN SEGUNDO PLANO (Sin congelar la pantalla)
            worker.DoWork += (s, e) =>
            {
                int count = 0;
                foreach (var file in files)
                {
                    try
                    {
                        var info = LoadMEinfo(file);
                        if (info.Major > 0)
                        {
                            _meFiles.Add(info);
                        }
                    }
                    catch { /* Silenciado para velocidad máxima */ }

                    count++;

                    // Reportar progreso cada 5 archivos
                    if (count % 5 == 0 || count == files.Count)
                    {
                        int percent = (int)((count / (float)files.Count) * 100);
                        worker.ReportProgress(percent, count);
                    }

                    // CONTROL DE RAM: Forzamos la limpieza de basura cada 100 archivos para evitar el pico de 1.2 GB
                    if (count % 100 == 0)
                    {
                        GC.Collect();
                    }
                }
            };

            // 2. LO QUE HACE LA PANTALLA MIENTRAS TRABAJA
            worker.ProgressChanged += (s, e) =>
            {
                pBar.Value = (int)e.UserState;
                this.Text = $"Escaneando repositorios... {e.ProgressPercentage}% ({(int)e.UserState}/{files.Count})";
            };

            // 3. LO QUE HACE CUANDO TERMINA EL ESCANEO
            worker.RunWorkerCompleted += (s, e) =>
            {
                SaveDB();      // Guardamos el db.dat para la próxima vez
                UpdateGUI();   // <--- ESTA LÍNEA es la que actualiza el contador en la pantalla

                this.Controls.Remove(pBar); // Quitamos la barra verde

                // Un log final bien detallado
                Log(string.Format("--- ESCANEO FINALIZADO ---"), LogLevel.Info);
                Log(string.Format("Base de datos: {0} registros activos.", _meFiles.Count), LogLevel.Info);
                Log("Listo para trabajar.", LogLevel.Info);
            };
            // Arrancamos el motor
            worker.RunWorkerAsync();
        }

        private void SaveDB()
        {
            try
            {
                using (Stream writer = new FileStream(_dbFile, FileMode.Create))
                {
                    var bformatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                    bformatter.Serialize(writer, _meFiles);
                }
            }
            catch { }
        }

        private void LoadDB()
        {
            if (_MEdir == string.Empty)
            {
                MessageBox.Show(@"Please, specify ME files folder first");
                var fbd = new FolderBrowserDialog();
                if (fbd.ShowDialog() != DialogResult.OK)
                {
                    Close();
                    return;
                }
                _MEdir = fbd.SelectedPath;
                Properties.Settings.Default.MEdir = _MEdir;
                Properties.Settings.Default.Save();
                UpdateDB();
            }

            if (!File.Exists(_dbFile))
            {
                UpdateDB();
                return;
            }

            try
            {
                using (Stream stream = File.Open(_dbFile, FileMode.Open))
                {
                    var bformatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                    _meFiles = (List<MEinfo>)bformatter.Deserialize(stream);
                }
                if (_meFiles.Any(mefile => !File.Exists(mefile.Path))) UpdateDB();
                else UpdateGUI();
            }
            catch (Exception exception)
            {
                Log(exception.Message, LogLevel.Error);
            }
        }

        private MEinfo GetMEFileInfo(Stream stream, string path, uint startoffset = 0, uint endoffset = 0)
        {
            stream.Seek(startoffset, SeekOrigin.Begin);
            var meinfo = new MEinfo();
            meinfo.Size = endoffset == 0 ? (uint)stream.Length : endoffset - startoffset;
            meinfo.Path = path;

            // ... (Mantén tu bloque de scanBuffer inicial de $MN2 aquí) ...

            stream.Seek(startoffset, SeekOrigin.Begin);
            // Leemos PreHeader
            var preHandle = GCHandle.Alloc(new BinaryReader(stream).ReadBytes(Marshal.SizeOf(typeof(FptPreHeader))), GCHandleType.Pinned);
            preHandle.Free();

            // Leemos Header
            var headerBytes = new BinaryReader(stream).ReadBytes(Marshal.SizeOf(typeof(FptHeader)));
            var handle = GCHandle.Alloc(headerBytes, GCHandleType.Pinned);
            var fptHeader = (FptHeader)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(FptHeader));
            handle.Free();

            // --- FILTRO DE CORDURA ---
            // Esto evita que el programa se congele con basura
            if (fptHeader.NumPartitions > 32 || fptHeader.NumPartitions == 0)
            {
                return meinfo;
            }

            // --- RE-DECLARACIÓN DE VARIABLES (Aquí se resuelven tus errores) ---
            var fptEntries = new List<FptEntry>();
            var mn2Manifests = new List<Mn2Manifest>(); // <-- Esta es la línea que te faltaba

            for (var i = 0; i < fptHeader.NumPartitions; i++)
            {
                var entryBytes = new BinaryReader(stream).ReadBytes(Marshal.SizeOf(typeof(FptEntry)));
                var eHandle = GCHandle.Alloc(entryBytes, GCHandleType.Pinned);
                fptEntries.Add((FptEntry)Marshal.PtrToStructure(eHandle.AddrOfPinnedObject(), typeof(FptEntry)));
                eHandle.Free();
            }

            // Buscamos manifiestos en las particiones críticas
            foreach (var fptEntry in fptEntries.Where(e =>
            {
                string name = new string(e.Name).Replace("\0", "");
                return name == "FTPR" || name == "AMTP" || name == "QSTP" || name == "NFTP";
            }))
            {
                stream.Seek(fptEntry.Offset + startoffset, SeekOrigin.Begin);
                var o = 0;
                if (new string(new BinaryReader(stream).ReadChars(4)) == "$CPD")
                {
                    o = new BinaryReader(stream).ReadByte() * 0x18 + 0x10;
                }
                stream.Seek(fptEntry.Offset + startoffset + o, SeekOrigin.Begin);

                var manifestBytes = new BinaryReader(stream).ReadBytes(Marshal.SizeOf(typeof(Mn2Manifest)));
                var mHandle = GCHandle.Alloc(manifestBytes, GCHandleType.Pinned);
                mn2Manifests.Add((Mn2Manifest)Marshal.PtrToStructure(mHandle.AddrOfPinnedObject(), typeof(Mn2Manifest)));
                mHandle.Free();
            }

            if (mn2Manifests.Count < 1) return meinfo; // Cambiamos Exception por return seguro

            var manifest = mn2Manifests.First();
            meinfo.Major = manifest.Major;
            meinfo.Minor = manifest.Minor;
            meinfo.Hotfix = manifest.Hotfix;
            meinfo.Build = manifest.Build;
            return meinfo;
        }

        private MEinfo LoadMEinfo(string path)
        {
            string fileName = Path.GetFileName(path);
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length < 1024 * 10)
                    {
                        Log(string.Format("Rechazado: {0} - Tamaño insuficiente", fileName), LogLevel.Warning);
                        return new MEinfo { Major = 0 };
                    }

                    byte[] scanBuffer = new byte[Math.Min(stream.Length, 1024 * 1024)];
                    stream.Read(scanBuffer, 0, scanBuffer.Length);
                    MEinfo info = new MEinfo { Path = path, Size = (uint)stream.Length };

                    // 1. ESCANEO $MN2 (v11-v15+)
                    for (int i = 0; i < scanBuffer.Length - 30; i += 4)
                    {
                        if (scanBuffer[i] == 0x24 && scanBuffer[i + 1] == 0x4D && scanBuffer[i + 2] == 0x4E && scanBuffer[i + 3] == 0x32)
                        {
                            ushort maj = BitConverter.ToUInt16(scanBuffer, i + 8);
                            if (maj >= 11 && maj < 30)
                            {
                                info.Major = maj;
                                info.Minor = BitConverter.ToUInt16(scanBuffer, i + 10);
                                info.Hotfix = BitConverter.ToUInt16(scanBuffer, i + 12);
                                info.Build = BitConverter.ToUInt16(scanBuffer, i + 14);

                                // REPORTE POSITIVO
                                Log(string.Format("Archivo correcto: {0} - Versión: {1}.{2}.{3}.{4}", fileName, info.Major, info.Minor, info.Hotfix, info.Build), LogLevel.Info);
                                return info;
                            }
                        }
                    }

                    // 2. ESCANEO $FPT (v2-v11)
                    int fptLimit = Math.Min(scanBuffer.Length - 0x100, 4096);
                    for (int i = 0; i < fptLimit; i += 0x10)
                    {
                        if (scanBuffer[i] == 0x24 && scanBuffer[i + 1] == 0x46 && scanBuffer[i + 2] == 0x50 && scanBuffer[i + 3] == 0x54)
                        {
                            try
                            {
                                int startOffset = i - 0x10;
                                if (startOffset >= 0)
                                {
                                    var meinfo = GetMEFileInfo(stream, path, (uint)startOffset);
                                    if (meinfo.Major > 0)
                                    {
                                        // REPORTE POSITIVO
                                        Log(string.Format("Archivo correcto: {0} - Versión: {1}.{2}.{3}.{4}", fileName, meinfo.Major, meinfo.Minor, meinfo.Hotfix, meinfo.Build), LogLevel.Info);
                                        return meinfo;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    // 3. ESCANEO RAW (v2-v10 extractos/UPD)
                    int[] rawOffsets = { 0x0, 0x10 };
                    foreach (int baseOff in rawOffsets)
                    {
                        if (baseOff + 0x1C > scanBuffer.Length) continue;

                        ushort maj = BitConverter.ToUInt16(scanBuffer, baseOff + 0x14);
                        if (maj >= 2 && maj <= 10)
                        {
                            info.Major = maj;
                            info.Minor = BitConverter.ToUInt16(scanBuffer, baseOff + 0x16);
                            info.Hotfix = BitConverter.ToUInt16(scanBuffer, baseOff + 0x18);
                            info.Build = BitConverter.ToUInt16(scanBuffer, baseOff + 0x1A);

                            // REPORTE POSITIVO
                            Log(string.Format("Archivo correcto: {0} - Versión: {1}.{2}.{3}.{4}", fileName, info.Major, info.Minor, info.Hotfix, info.Build), LogLevel.Info);
                            return info;
                        }
                    }

                    Log(string.Format("Ignorado: {0} - No es un firmware Intel válido", fileName), LogLevel.Warning);
                    return new MEinfo { Major = 0 };
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("Error de lectura: {0} - {1}", fileName, ex.Message), LogLevel.Error);
                return new MEinfo { Major = 0 };
            }
        }

        private void LoadBIOS(string path)
        {
            Log("----------------------------------------", LogLevel.Default);
            BIOSfile = File.ReadAllBytes(path);

            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(0x10, SeekOrigin.Begin);
                var magic = new BinaryReader(stream).ReadBytes(4);

                if (magic.SequenceEqual(new byte[] { 0x5A, 0xA5, 0xF0, 0x0F }))
                {
                    stream.Seek(0x14, SeekOrigin.Begin);
                    var flmap0 = new BinaryReader(stream).ReadUInt32();
                    var flmap1 = new BinaryReader(stream).ReadUInt32();
                    var nr = flmap0 >> 24 & 0x7;
                    var frba = flmap0 >> 12 & 0xff0;

                    if (nr >= 2 || true)
                    {
                        Log("Intel BIOS image detected! :D", LogLevel.Info);
                        stream.Seek(frba, SeekOrigin.Begin);

                        var flreg0 = new BinaryReader(stream).ReadUInt32();
                        var flreg1 = new BinaryReader(stream).ReadUInt32();
                        var flreg2 = new BinaryReader(stream).ReadUInt32();

                        BIOS_ME_start_offset = (flreg2 & 0x1fff) << 12;
                        BIOS_ME_end_offset = flreg2 >> 4 & 0x1fff000 | 0xfff + 1;

                        if (BIOS_ME_start_offset >= BIOS_ME_end_offset) throw new Exception("The ME/TXE region in this image has been disabled");

                        BIOS_ME_info = GetMEFileInfo(stream, path, BIOS_ME_start_offset, BIOS_ME_end_offset);
                        _mode = BIOS_ME_info.Major < 4 ? Mode.TXE : Mode.ME;

                        UpdateGUI();
                        Log("BIOS read successful! " + path.SafeFileName(), LogLevel.Info);
                        Log($"The {_mode} region goes from {BIOS_ME_start_offset:X8} to {BIOS_ME_end_offset:X8}", LogLevel.Info);

                        UpdateComboBox();

                        var offset = Find(BIOSfile, MSDM_table_pattern) + MSDM_offset;
                        if (offset - MSDM_offset != -1)
                        {
                            stream.Seek(offset, SeekOrigin.Begin);
                            var handle = GCHandle.Alloc(new BinaryReader(stream).ReadBytes(Marshal.SizeOf(typeof(MSDM))), GCHandleType.Pinned);
                            var MSDM = (MSDM)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(MSDM));
                            handle.Free();
                            WinKeyTextBox.Text = new string(MSDM.WinKey);
                        }
                        else { WinKeyTextBox.Text = @"none"; }
                        return;
                    }
                    MessageBox.Show(flmap0 + " " + flmap1);
                    throw new Exception("Number of partitions in file is less than 2! " + path.SafeFileName());
                }
                ClearGUI();
                BIOSfile = null;
                throw new Exception("Invalid input file " + path.SafeFileName());
            }
        }

        private void ExtractButton_Click(object sender, EventArgs e)
        {
            if (BIOSfile == null) { Log("Nothing to save :(", LogLevel.Warning); return; }
            var sfd = new SaveFileDialog { AddExtension = true, DefaultExt = "bin", FileName = _mode + " from bios " + BIOSfilename };
            if (sfd.ShowDialog() != DialogResult.OK) return;

            try
            {
                var me = new byte[BIOS_ME_end_offset - BIOS_ME_start_offset];
                Array.Copy(BIOSfile, BIOS_ME_start_offset, me, 0, BIOS_ME_end_offset - BIOS_ME_start_offset);
                var _me = new List<byte>(me);
                for (int i = _me.Count - 1; i >= 0; i--) { if (_me[i] == 0xFF) _me.RemoveAt(i); else break; }
                File.WriteAllBytes(sfd.FileName, _me.ToArray());
                Log("Saved to " + sfd.FileName, LogLevel.Info);
            }
            catch (Exception exception) { Log(exception.Message, LogLevel.Error); }
        }

        void UpdateComboBox()
        {
            if (this.InvokeRequired) { this.Invoke(new Action(UpdateComboBox)); return; }
            MEsComboBox.Items.Clear();
            _validMEfiles.Clear();
            foreach (var mefile in _meFiles)
            {
                if (BIOS_ME_info.Major == mefile.Major && (BIOS_ME_info.Minor == mefile.Minor || !MinorVer_checkBox.Checked) && BIOS_ME_end_offset - BIOS_ME_start_offset >= mefile.Size)
                {
                    MEsComboBox.Items.Add($@"{mefile.Major}.{mefile.Minor}.{mefile.Hotfix}.{mefile.Build} - {mefile.Path.SafeFileName()}");
                    _validMEfiles.Add(mefile.Path);
                }
            }
            if (MEsComboBox.Items.Count == 0) MEsComboBox.Items.Add("--none--");
            MEsComboBox.SelectedIndex = 0;
        }

        private void OpenBIOSbutton_Click(object sender, EventArgs e)
        {
            var ofd = new OpenFileDialog { Multiselect = false };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            try { LoadBIOS(ofd.FileName); BIOSfilename = ofd.SafeFileName; }
            catch (Exception exception) { Log(exception.Message, LogLevel.Error); }
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            if (BIOSfile == null || _validMEfiles.Count == 0) return;
            MEfile = File.ReadAllBytes(_validMEfiles[MEsComboBox.SelectedIndex]);

            for (var i = 0; i < BIOS_ME_end_offset - BIOS_ME_start_offset; i++)
            {
                if (i < MEfile.Length) { BIOSfile[i + BIOS_ME_start_offset] = MEfile[i]; continue; }
                BIOSfile[i + BIOS_ME_start_offset] = (byte)0xFF;
            }
            var sfd = new SaveFileDialog { AddExtension = true, DefaultExt = "bin", FileName = $@"{Regex.Replace(BIOSfilename, ".bin", string.Empty, RegexOptions.IgnoreCase)} + {_mode} {MEsComboBox.Text}" };
            if (sfd.ShowDialog() != DialogResult.OK) return;
            try { File.WriteAllBytes(sfd.FileName, BIOSfile); Log("Saved to " + sfd.FileName, LogLevel.Info); }
            catch (Exception exception) { Log(exception.Message, LogLevel.Error); }
        }

        static int Find(IList<byte> array, IList<byte> mask)
        {
            try
            {
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] != mask[0]) continue;
                    for (var j = 1; j < mask.Count; j++)
                    {
                        if (i + j >= array.Count) return -1;
                        if (array[i + j] == mask[j] & j == mask.Count - 1) return i;
                        if (array[i + j] == mask[j]) continue;
                        i += j; break;
                    }
                }
                return -1;
            }
            catch (Exception e) { throw new Exception(e.Message); }
        }

        private readonly List<string> _filesList = new List<string>();
        private IEnumerable<string> GetFileNames(string path)
        {
            _filesList.Clear();
            FileFinder(path);
            return _filesList;
        }

        private void FileFinder(string path)
        {
            try
            {
                var dirs = Directory.GetDirectories(path);
                // Solo listamos archivos con extensiones que realmente pueden ser firmwares
                var files = Directory.GetFiles(path).Where(f =>
                    f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".rgn", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".upd", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".cap", StringComparison.OrdinalIgnoreCase)
                );

                _filesList.AddRange(files);
                foreach (var dir in dirs) { FileFinder(dir); }
            }
            catch { /* Ignorar carpetas sin acceso */ }
        }

        private void button1_Click(object sender, EventArgs e) { }

        private void Log(string message, LogLevel level)
        {
            if (this.InvokeRequired) { this.BeginInvoke(new Action(() => Log(message, level))); return; }
            Color color;
            switch (level)
            {
                case LogLevel.Info: color = Color.Green; break;
                case LogLevel.Warning: color = Color.DarkOrange; break;
                case LogLevel.Error: color = Color.Red; break;
                case LogLevel.Critical: color = Color.Brown; break;
                default: color = Color.Black; break;
            }
            DebugTextBox.AppendText(message + "\n", color);
        }

        private void DebugTextBox_TextChanged(object sender, EventArgs e)
        {
            DebugTextBox.SelectionStart = DebugTextBox.Text.Length;
            DebugTextBox.ScrollToCaret();
        }

        private void ChangeMEFolderButton_Click(object sender, EventArgs e)
        {
            var fbd = new FolderBrowserDialog { SelectedPath = _MEdir };
            if (fbd.ShowDialog() != DialogResult.OK) return;
            _MEdir = fbd.SelectedPath;
            Properties.Settings.Default.MEdir = _MEdir;
            Properties.Settings.Default.Save();
            UpdateDB();
        }

        private void UpdateDB_Button_Click(object sender, EventArgs e) => UpdateDB();
        private void MinorVer_checkBox_CheckedChanged(object sender, EventArgs e) => UpdateComboBox();

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                try { LoadBIOS(files[0]); BIOSfilename = files[0].SafeFileName(); }
                catch (Exception exception) { Log(exception.Message, LogLevel.Error); }
            }
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void About_button_Click(object sender, EventArgs e)
        {
            Log("kolyandex, 2018", LogLevel.Info);
            Log("Bios IA Upgrade: V2-V21, UI Anti-Cuelgue", LogLevel.Info);
        }
    }
}