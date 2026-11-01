using System.Net.Http;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpencodeProviderManager;

static class Program
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string GlobalPath = Path.Combine(Home, ".config", "opencode", "opencode.json");
    static readonly string LocalPath = Path.Combine(Home, ".local", "share", "opencode", "opencode.json");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // id, имя, порт, ollama?
    static readonly (string Id, string Name, int Port, bool Ollama)[] Known =
    {
        ("ollama",   "Ollama",       11434, true),
        ("lmstudio", "LM Studio",     1234, false),
        ("llamacpp", "llama.cpp",     8080, false),
        ("vllm",     "vLLM",          8000, false),
        ("tgi",      "TGI",           8080, false),
        ("koboldcpp","KoboldCpp",     5001, false),
        ("localai",  "LocalAI",       7860, false),
    };

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    // ---------- JSON ----------
    static JsonObject ReadJson(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    static void WriteJson(string path, JsonObject node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            File.Copy(path, $"{path}.bak_{DateTime.Now:yyyyMMdd_HHmmss}", true);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }

    static string S(JsonNode? n) => n?.GetValue<string>() ?? "";
    static JsonArray? Models(JsonObject p) => p["models"] as JsonArray;

    // Модели: унифицированный вид [{key, modelID, name, extra:{...прочие поля}}]
    static JsonArray ModelsToUnified(JsonNode? node)
    {
        var arr = new JsonArray();
        if (node is JsonObject mo)
            foreach (var (k, m) in mo)
            {
                var item = new JsonObject { ["key"] = k };
                if (m is JsonObject mm)
                {
                    var extra = new JsonObject();
                    foreach (var (pk, pv) in mm)
                    {
                        if (pk is "modelID" or "name") item[pk] = pv?.DeepClone();
                        else extra[pk] = pv?.DeepClone();
                    }
                    if (extra.Count > 0) item["extra"] = extra;
                }
                arr.Add(item);
            }
        return arr;
    }

    static JsonObject UnifiedToModels(JsonNode? arr)
    {
        var mo = new JsonObject();
        if (arr is not JsonArray list) return mo;
        foreach (var n in list)
        {
            if (n is not JsonObject it) continue;
            var key = S(it["key"]);
            if (key.Length == 0) continue;
            var m = new JsonObject();
            if (it["modelID"] is JsonNode mid && S(mid).Length > 0) m["modelID"] = mid.DeepClone();
            m["name"] = it["name"]?.DeepClone() ?? key;
            if (it["extra"] is JsonObject ex)
                foreach (var (k, v) in ex) m[k] = v?.DeepClone();
            mo[key] = m;
        }
        return mo;
    }

    // Читает оба конфига → словарь pid -> унифицированный JsonObject
    static Dictionary<string, JsonObject> LoadProviders()
    {
        var result = new Dictionary<string, JsonObject>();
        foreach (var (path, key, optKey, pkgKey) in new[]
        {
            (GlobalPath, "providers", "settings", "package"),
            (LocalPath,  "provider",  "options",  "npm")
        })
        {
            if (ReadJson(path)[key] is not JsonObject provs) continue;
            foreach (var (pid, node) in provs)
            {
                if (node is not JsonObject p) continue;
                var opts = p[optKey] as JsonObject ?? new JsonObject();
                result[pid] = new JsonObject
                {
                    ["name"] = S(p["name"]) is { Length: > 0 } n ? n : pid,
                    ["baseURL"] = S(opts["baseURL"]),
                    ["apiKey"] = S(opts["apiKey"]),
                    ["package"] = S(p[pkgKey]),
                    ["models"] = ModelsToUnified(p["models"])
                };
            }
        }
        return result;
    }

    static void SaveProviders(Dictionary<string, JsonObject> provs)
    {
        var g = ReadJson(GlobalPath);
        var gp = new JsonObject();
        var l = ReadJson(LocalPath);
        var lp = new JsonObject();
        foreach (var (pid, p) in provs)
        {
            var pkg = S(p["package"]);
            var opts = new JsonObject { ["baseURL"] = p["baseURL"]?.DeepClone(), ["apiKey"] = p["apiKey"]?.DeepClone() };
            var models = UnifiedToModels(p["models"]);
            gp[pid] = new JsonObject
            {
                ["name"] = p["name"]?.DeepClone(),
                ["package"] = pkg.Length > 0 ? pkg : "@opencode/ai/providers/openai-compatible",
                ["settings"] = opts.DeepClone(),
                ["models"] = models.DeepClone()
            };
            lp[pid] = new JsonObject
            {
                ["name"] = p["name"]?.DeepClone(),
                ["npm"] = pkg.Length > 0 ? pkg : "@ai-sdk/openai-compatible",
                ["options"] = opts.DeepClone(),
                ["models"] = models.DeepClone()
            };
        }
        g["providers"] = gp;
        WriteJson(GlobalPath, g);
        l["provider"] = lp;
        WriteJson(LocalPath, l);
    }

    // ---------- Поиск локальных ----------
    static async Task<(bool ok, List<string> models)> Probe(int port, bool ollama)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(800);
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
        }
        catch { return (false, new List<string>()); }

        var url = $"http://127.0.0.1:{port}" + (ollama ? "/api/tags" : "/v1/models");
        try
        {
            var json = await Http.GetStringAsync(url);
            var node = JsonNode.Parse(json);
            var arr = ollama ? node?["models"] : node?["data"];
            var list = new List<string>();
            if (arr is JsonArray a)
                foreach (var it in a)
                {
                    var id = ollama ? S(it?["name"]) : S(it?["id"]);
                    if (id.Length > 0) list.Add(id);
                }
            return (true, list);
        }
        catch { return (true, new List<string>()); }
    }

    // ---------- Главная форма ----------
    class MainForm : Form
    {
        readonly Dictionary<string, JsonObject> _provs = new();
        readonly ListView _lv = new(), _scan = new();
        readonly Button _btnScan = new(), _btnAddFound = new();
        readonly Label _status = new();

        public MainForm()
        {
            Text = "OpenCode Local Provider Manager";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1040, 660);
            MinimumSize = new Size(900, 560);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4) };
            top.Controls.Add(MkBtn("Обновить", (s, e) => Reload()));
            _btnScan = MkBtn("Поиск локальных", (s, e) => Scan());
            top.Controls.Add(_btnScan);
            top.Controls.Add(MkBtn("Добавить вручную", (s, e) => EditDialog(null)));
            var save = MkBtn("Сохранить конфиг", (s, e) => DoSave());
            top.Controls.Add(save);

            _status.Dock = DockStyle.Bottom;
            _status.Height = 26;
            _status.BorderStyle = BorderStyle.Fixed3D;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Text = "Готово";

            var split = new SplitContainer { Dock = DockStyle.Fill };
            InitList(_lv, new[] { "ID", "Название", "baseURL", "API ключ", "Модели" }, new[] { 110, 150, 250, 160, 60 });
            InitList(_scan, new[] { "ID", "Название", "Порт", "Статус" }, new[] { 90, 150, 60, 110 });
            split.Panel1.Controls.Add(ListPanel(_lv, "Текущие провайдеры в OpenCode",
                MkBtn("Редактировать", (s, e) => EditCurrent()), MkBtn("Удалить", (s, e) => Delete())));
            split.Panel2.Controls.Add(ListPanel(_scan, "Найденные локальные провайдеры",
                _btnAddFound = MkBtn("Добавить", (s, e) => AddFound()),
                MkBtn("Очистить", (s, e) => _scan.Items.Clear())));

            Controls.Add(split);
            Controls.Add(_status);
            Controls.Add(top);

            Reload();
        }

        static Button MkBtn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += onClick;
            return b;
        }

        static void InitList(ListView lv, string[] cols, int[] widths)
        {
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.Dock = DockStyle.Fill;
            for (int i = 0; i < cols.Length; i++)
                lv.Columns.Add(cols[i], widths[i]);
        }

        static Panel ListPanel(ListView lv, string title, params Control[] buttons)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var group = new GroupBox { Text = title, Dock = DockStyle.Fill };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
            foreach (var b in buttons) bottom.Controls.Add(b);
            group.Controls.Add(lv);
            group.Controls.Add(bottom);
            p.Controls.Add(group);
            return p;
        }

        void Reload()
        {
            _provs.Clear();
            foreach (var p in LoadProviders()) _provs[p.Key] = p.Value;
            FillList();
            _status.Text = $"Загружено провайдеров: {_provs.Count}";
        }

        // Немедленная запись изменений на диск (без всплывающих окон)
        void Persist(string msg)
        {
            try
            {
                SaveProviders(_provs);
                _status.Text = msg;
            }
            catch (Exception ex) { Error($"Не удалось сохранить конфиги: {ex.Message}"); }
        }

        void FillList()
        {
            _lv.BeginUpdate();
            _lv.Items.Clear();
            foreach (var (pid, p) in _provs)
                _lv.Items.Add(new ListViewItem(new[]
                {
                    pid, S(p["name"]), S(p["baseURL"]), S(p["apiKey"]),
                    (Models(p)?.Count ?? 0).ToString()
                }));
            _lv.EndUpdate();
        }

        void Scan()
        {
            _btnScan.Enabled = false;
            _status.Text = "Сканирование локальных портов...";
            _scan.Items.Clear();
            var known = Known;
            Task.Run(async () =>
            {
                var cache = new Dictionary<int, (bool ok, List<string> models)>();
                foreach (var k in known)
                {
                    if (!cache.TryGetValue(k.Port, out var r))
                        cache[k.Port] = r = await Probe(k.Port, k.Ollama);
                    var item = new ListViewItem(new[]
                    {
                        k.Id, k.Name, k.Port.ToString(),
                        r.ok ? $"Найдено ({r.models.Count} мод.)" : "Не отвечает"
                    });
                    if (r.ok) item.Tag = (k.Id, k.Name, k.Port, k.Ollama, r.models);
                    Invoke(() => _scan.Items.Add(item));
                }
                var found = cache.Values.Count(v => v.ok);
                Invoke(() => { _status.Text = $"Сканирование завершено. Найдено: {found}"; _btnScan.Enabled = true; });
            });
        }

        void AddFound()
        {
            if (_scan.SelectedItems.Count == 0) { Warn("Выберите провайдера из списка найденных."); return; }
            if (_scan.SelectedItems[0].Tag is not ValueTuple<string, string, int, bool, List<string>> tag) return;
            var (id, name, port, ollama, models) = tag;
            if (_provs.ContainsKey(id) && !Ask($"Провайдер '{id}' уже существует. Перезаписать?")) return;

            var url = $"http://127.0.0.1:{port}" + (ollama ? "" : "/v1");
            var arr = new JsonArray();
            if (models.Count > 0)
                foreach (var m in models) arr.Add(UModel(m, m));
            else arr.Add(UModel("local-model", "Local Model"));

            _provs[id] = new JsonObject
            {
                ["name"] = name,
                ["baseURL"] = url,
                ["apiKey"] = "not-needed",
                ["package"] = "@opencode/ai/providers/openai-compatible",
                ["models"] = arr
            };
            FillList();
            Persist($"Добавлен провайдер: {id} (моделей: {arr.Count})");
        }

        static JsonObject UModel(string key, string name) => new()
        {
            ["key"] = key, ["modelID"] = key, ["name"] = name
        };

        void EditCurrent()
        {
            if (_lv.SelectedItems.Count == 0) { Warn("Выберите провайдера из списка текущих."); return; }
            EditDialog(_lv.SelectedItems[0].Text);
        }

        void Delete()
        {
            if (_lv.SelectedItems.Count == 0) return;
            var pid = _lv.SelectedItems[0].Text;
            if (!Ask($"Удалить провайдер '{pid}' из обоих конфигов?")) return;
            _provs.Remove(pid);
            FillList();
            Persist($"Удалён провайдер: {pid}. Конфиги сохранены.");
        }

        void DoSave()
        {
            try
            {
                SaveProviders(_provs);
                Info($"Конфиги сохранены:\n{GlobalPath}\n{LocalPath}");
                _status.Text = "Конфиги сохранены";
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        // Диалог провайдера (null = добавление)
        void EditDialog(string? pid)
        {
            using var dlg = new ProviderDialog(pid, pid != null ? _provs.GetValueOrDefault(pid) : null);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (pid != null && pid != dlg.ProviderId) _provs.Remove(pid);
            _provs[dlg.ProviderId] = dlg.Result;
            FillList();
            Persist($"Сохранён провайдер: {dlg.ProviderId}");
        }

        void Warn(string msg) => MessageBox.Show(this, msg, "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        static void Info(string msg) => MessageBox.Show(msg, "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
        static void Error(string msg) => MessageBox.Show(msg, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        static bool Ask(string msg) => MessageBox.Show(msg, "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    // ---------- Диалог провайдера + модели ----------
    class ProviderDialog : Form
    {
        public string ProviderId = "";
        public JsonObject Result = new();

        readonly TextBox _tbId = new(), _tbName = new(), _tbUrl = new(), _tbKey = new(), _tbPkg = new();
        readonly ListView _models = new();
        readonly Dictionary<string, JsonObject> _work = new(); // key -> unified model

        public ProviderDialog(string? pid, JsonObject? p)
        {
            bool edit = p != null;
            Text = edit ? $"Редактировать провайдера: {pid}" : "Добавить провайдера";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(660, 700);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;

            if (p != null)
            {
                _tbName.Text = S(p["name"]);
                _tbUrl.Text = S(p["baseURL"]);
                _tbKey.Text = S(p["apiKey"]);
                _tbPkg.Text = S(p["package"]);
                if (Models(p) is JsonArray arr)
                    foreach (var n in arr)
                        if (n is JsonObject it)
                            _work[S(it["key"])] = (JsonObject)it.DeepClone();
            }
            _tbId.Text = pid ?? "";
            _tbId.ReadOnly = edit;

            int y = 12;
            y = Field("ID провайдера:", _tbId, y);
            y = Field("Название:", _tbName, y);
            y = Field("baseURL:", _tbUrl, y);
            y = Field("API ключ:", _tbKey, y);
            _tbKey.Width = 395;
            var btnKeyFile = new Button { Text = "Файл...", Left = 566, Top = y - 36, Width = 64 };
            btnKeyFile.Click += (s, e) => EditKeyFile();
            Controls.Add(btnKeyFile);
            y = Field("Пакет (package/npm):", _tbPkg, y);

            var group = new GroupBox { Text = "Модели провайдера", Left = 10, Top = y, Width = 620, Height = 440, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            _models.View = View.Details;
            _models.FullRowSelect = true;
            _models.HideSelection = false;
            _models.Bounds = new Rectangle(10, 20, 595, 350);
            _models.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _models.Columns.Add("Ключ", 150);
            _models.Columns.Add("modelID", 230);
            _models.Columns.Add("Название", 190);
            group.Controls.Add(_models);

            var btns = new FlowLayoutPanel { Left = 10, Top = 378, Width = 595, Height = 40, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            btns.Controls.Add(MkBtn("Добавить модель", (s, e) => ModelDialog("Добавить модель", null)));
            btns.Controls.Add(MkBtn("Редактировать модель", (s, e) => ModelDialog("Редактировать модель", _models.SelectedItems.Count > 0 ? _models.SelectedItems[0] : null)));
            btns.Controls.Add(MkBtn("Удалить модель", (s, e) => DeleteModel()));
            group.Controls.Add(btns);
            Controls.Add(group);

            var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Left = 10, Width = 110, Top = y + 448, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 130, Width = 110, Top = ok.Top, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            ok.Click += (s, e) => OnOk();
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            Refill();
        }

        int Field(string label, TextBox tb, int y)
        {
            Controls.Add(new Label { Text = label, Left = 10, Top = y + 4, Width = 150 });
            tb.Left = 165; tb.Top = y; tb.Width = 465; tb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(tb);
            return y + 34;
        }

        static Button MkBtn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += onClick;
            return b;
        }

        // Редактирование файла ключа по ссылке {file:путь}
        void EditKeyFile()
        {
            var val = _tbKey.Text.Trim();
            if (!val.StartsWith("{file:") || !val.EndsWith("}"))
            {
                MessageBox.Show(this,
                    "Ключ хранится прямо в конфиге — правьте поле выше.\n" +
                    "Кнопка «Файл...» работает для ссылок вида {file:путь}.",
                    "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var path = val[6..^1].Trim();
            if (path.StartsWith("~"))
                path = Path.Combine(Home, path[1..].TrimStart('/', '\\'));

            using var dlg = new Form
            {
                Text = "Ключ: " + Path.GetFileName(path),
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(560, 260),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false
            };
            var tb = new TextBox
            {
                Multiline = true, Left = 12, Top = 12, Width = 520, Height = 150,
                ScrollBars = ScrollBars.Vertical,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            try { tb.Text = File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка чтения"); return; }

            var okB = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Left = 340, Top = 175, Width = 90, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var cB = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 440, Top = 175, Width = 90, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            okB.Click += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, tb.Text.Trim());
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dlg, "Не удалось записать файл: " + ex.Message, "Ошибка",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    dlg.DialogResult = DialogResult.None; // окно остаётся открытым
                }
            };
            dlg.Controls.Add(tb); dlg.Controls.Add(okB); dlg.Controls.Add(cB);
            dlg.AcceptButton = okB; dlg.CancelButton = cB;
            dlg.ShowDialog(this);
        }

        void Refill()
        {
            _models.BeginUpdate();
            _models.Items.Clear();
            foreach (var m in _work.Values)
                _models.Items.Add(new ListViewItem(new[] { S(m["key"]), S(m["modelID"]), S(m["name"]) }) { Tag = m });
            _models.EndUpdate();
        }

        void ModelDialog(string title, ListViewItem? item)
        {
            var cur = item?.Tag as JsonObject;
            using var dlg = new Form
            {
                Text = title, StartPosition = FormStartPosition.CenterParent,
                Size = new Size(460, 240), FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = MinimizeBox = false
            };
            var tbK = new TextBox();
            var tbM = new TextBox();
            var tbN = new TextBox();
            if (cur != null) { tbK.Text = S(cur["key"]); tbM.Text = S(cur["modelID"]); tbN.Text = S(cur["name"]); }
            int y = 14;
            void F(string lbl, TextBox tb) { dlg.Controls.Add(new Label { Text = lbl, Left = 12, Top = y + 4, Width = 150 }); tb.Left = 165; tb.Top = y; tb.Width = 260; dlg.Controls.Add(tb); y += 36; }
            F("Ключ модели:", tbK);
            F("modelID (опц.):", tbM);
            F("Название:", tbN);
            var okB = new Button { Text = "ОК", DialogResult = DialogResult.OK, Left = 165, Top = y + 4, Width = 90 };
            var cB = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 265, Top = y + 4, Width = 90 };
            dlg.Controls.Add(okB); dlg.Controls.Add(cB);
            dlg.AcceptButton = okB; dlg.CancelButton = cB;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string key = tbK.Text.Trim(), mid = tbM.Text.Trim(), name = tbN.Text.Trim();
            if (key.Length == 0) { MessageBox.Show(this, "Заполните ключ модели.", "Внимание"); return; }
            if (name.Length == 0) name = key;

            var nu = new JsonObject { ["key"] = key, ["modelID"] = mid, ["name"] = name };
            if (cur != null)
            {
                // сохраняем все прочие поля исходной модели (limit и т.д.)
                var ex = cur["extra"] as JsonObject ?? new JsonObject();
                foreach (var (k, v) in cur)
                    if (k is not ("key" or "modelID" or "name" or "extra")) ex[k] = v?.DeepClone();
                if (ex.Count > 0) nu["extra"] = ex;
                _work.Remove(S(cur["key"]));
            }
            _work[key] = nu;
            Refill();
        }

        void DeleteModel()
        {
            if (_models.SelectedItems.Count == 0) return;
            var key = _models.SelectedItems[0].Text;
            if (MessageBox.Show(this, $"Удалить модель '{key}'?", "Удаление", MessageBoxButtons.YesNo) == DialogResult.Yes)
            { _work.Remove(key); Refill(); }
        }

        void OnOk()
        {
            string id = _tbId.Text.Trim(), url = _tbUrl.Text.Trim();
            if (id.Length == 0 || url.Length == 0)
            { MessageBox.Show(this, "Заполните ID и baseURL.", "Внимание"); DialogResult = DialogResult.None; return; }

            ProviderId = id;
            var arr = new JsonArray();
            foreach (var m in _work.Values) arr.Add(m.DeepClone());
            Result = new JsonObject
            {
                ["name"] = _tbName.Text.Trim(),
                ["baseURL"] = url,
                ["apiKey"] = _tbKey.Text.Trim(),
                ["package"] = _tbPkg.Text.Trim(),
                ["models"] = arr
            };
        }
    }
}
