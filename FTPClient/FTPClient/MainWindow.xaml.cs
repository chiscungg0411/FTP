using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FTPClient
{
    public partial class MainWindow : Window
    {
        private readonly SemaphoreSlim _streamSemaphore = new SemaphoreSlim(1, 1);
        private TcpClient _client;
        private NetworkStream _networkStream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private bool _isUploading = false;
        private bool _isConnected = false;
        private string _currentLocalPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        private string _currentServerPath = "/";

        private ObservableCollection<FileItem> _serverItems = new ObservableCollection<FileItem>();
        private ObservableCollection<FileItem> _clientItems = new ObservableCollection<FileItem>();

        public MainWindow()
        {
            InitializeComponent();

            lstServerItems.ItemsSource = _serverItems;
            lstClientItems.ItemsSource = _clientItems;

            btnDownload.IsEnabled = false;
            btnDeleteServer.IsEnabled = false;
            btnUpload.IsEnabled = false;
            btnDeleteClient.IsEnabled = false;
            btnRefresh.IsEnabled = false;

            UpdateClientPath();
            LoadLocalItems();
        }

        public class FileItem
        {
            public string Name { get; set; }
            public string Icon { get; set; }
            public bool IsFolder { get; set; }
            public string FullPath { get; set; }
        }

        #region Helper Methods
        private async Task<T> WithTimeout<T>(Task<T> task, int timeoutMs, string errorMsg)
        {
            using (var cts = new CancellationTokenSource())
            {
                var delayTask = Task.Delay(timeoutMs, cts.Token);
                var completedTask = await Task.WhenAny(task, delayTask);
                if (completedTask == delayTask)
                {
                    Log(errorMsg + " (timeout)");
                    CleanupConnection();
                    throw new TimeoutException(errorMsg);
                }
                cts.Cancel();
                return await task;
            }
        }

        private async Task<string> ReadLineWithTimeoutAsync(int timeoutMs = 10000)
        {
            if (_reader == null) return null;
            return await WithTimeout(_reader.ReadLineAsync(), timeoutMs, "Timeout waiting for server response");
        }
        #endregion

        #region UI Logic
        private void UpdateClientPath()
        {
            txtCurrentClientPath.Text = _currentLocalPath;
        }

        private void lstServerItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasSelection = lstServerItems.SelectedItem != null;
            btnDownload.IsEnabled = hasSelection;
            btnDeleteServer.IsEnabled = hasSelection;

            if (hasSelection)
            {
                var selectedItem = (FileItem)lstServerItems.SelectedItem;
                Log($"Server item selected: {selectedItem.Name} ({(selectedItem.IsFolder ? "Folder" : "File")})");
            }
        }

        private void lstClientItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasSelection = lstClientItems.SelectedItem != null;
            var selectedItem = (FileItem)lstClientItems.SelectedItem;
            bool canPerformAction = hasSelection && (selectedItem?.Name != "..");
            btnUpload.IsEnabled = canPerformAction;
            btnDeleteClient.IsEnabled = canPerformAction;

            if (hasSelection && selectedItem != null)
            {
                Log($"Client item selected: {selectedItem.Name} ({(selectedItem.IsFolder ? "Folder" : "File")})");
            }
        }
        #endregion

        #region Connection and Navigation
        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txtLog.Clear();
                Log($"Connecting to server {txtServerIP.Text.Trim()}:2121...");

                if (_client == null || !_client.Connected)
                {
                    string serverIP = txtServerIP.Text.Trim();
                    if (string.IsNullOrEmpty(serverIP)) serverIP = "127.0.0.1";

                    _client = await Task.Run(() => CreateTcpClientWithTimeout(serverIP, 2121, 5000));
                    _networkStream = _client.GetStream();
                    _reader = new StreamReader(_networkStream, Encoding.UTF8);
                    _writer = new StreamWriter(_networkStream, Encoding.UTF8) { AutoFlush = true };

                    Log($"Connected to Server {serverIP}:2121 successfully!");
                    string welcome = await ReadLineWithTimeoutAsync();
                    Log("Server: " + welcome);

                    _isConnected = true;
                    btnRefresh.IsEnabled = true;
                    btnGoUp.IsEnabled = true; // Kích hoạt nút Go Up sau khi kết nối
                    await LoadServerItemsAsync();
                }
                else
                {
                    Log("Already connected to server!");
                }
            }
            catch (Exception ex)
            {
                Log("Connection error: " + ex.Message);
                CleanupConnection();
            }
        }

        private TcpClient CreateTcpClientWithTimeout(string host, int port, int timeoutMs)
        {
            var tcpClient = new TcpClient();
            var result = tcpClient.BeginConnect(host, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeoutMs));

            if (!success)
            {
                throw new TimeoutException("Connection timeout.");
            }
            tcpClient.EndConnect(result);
            return tcpClient;
        }

        private async void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadServerItemsAsync();
            LoadLocalItems();
        }

        private async Task LoadServerItemsAsync()
        {
            if (!CheckConnection() || _isUploading)
            {
                Log("Cannot load server items: Not connected or an operation is in progress.");
                return;
            }
            _serverItems.Clear();

            try
            {
                Dispatcher.Invoke(() => {
                    txtCurrentServerPath.Text = _currentServerPath;
                    // Kích hoạt nút Go Up khi không ở thư mục gốc
                    btnGoUp.IsEnabled = !string.IsNullOrEmpty(_currentServerPath) && _currentServerPath != "/";
                });

                await Task.Run(async () =>
                {
                    // Rest of the method remains unchanged...
                    await _streamSemaphore.WaitAsync();
                    try
                    {
                        if (!CheckConnection()) return;
                        _writer.WriteLine("LIST");
                        await _writer.FlushAsync();
                        Log("Sent LIST command to server...");
                    }
                    finally
                    {
                        _streamSemaphore.Release();
                    }

                    string line = await ReadLineWithTimeoutAsync();
                    Log("Server: " + line);

                    while (line != null)
                    {
                        line = await ReadLineWithTimeoutAsync();
                        if (line == "END_OF_LIST" || line == null) break;

                        if (line.StartsWith("[Folder] "))
                        {
                            string folderName = line.Substring("[Folder] ".Length).Trim();
                            Dispatcher.Invoke(() => _serverItems.Add(new FileItem { Name = folderName, Icon = "📁", IsFolder = true, FullPath = folderName }));
                        }
                        else if (line.StartsWith("[File] "))
                        {
                            string fileName = line.Substring("[File] ".Length).Trim();
                            Dispatcher.Invoke(() => _serverItems.Add(new FileItem { Name = fileName, Icon = "📄", IsFolder = false, FullPath = fileName }));
                        }
                    }
                });

                Log($"Loaded {_serverItems.Count} items from server.");

                // Kích hoạt nút CreateFolder sau khi load danh sách thành công
                Dispatcher.Invoke(() => btnCreateFolder.IsEnabled = true);
            }
            catch (Exception ex)
            {
                Log("Error loading server items: " + ex.Message);
                CleanupConnection();
            }
        }

        private void LoadLocalItems()
        {
            _clientItems.Clear();
            try
            {
                if (Directory.GetParent(_currentLocalPath) != null)
                {
                    _clientItems.Add(new FileItem { Name = "..", Icon = "⬆️", IsFolder = true, FullPath = ".." });
                }
                foreach (string dir in Directory.GetDirectories(_currentLocalPath))
                {
                    _clientItems.Add(new FileItem { Name = Path.GetFileName(dir), Icon = "📁", IsFolder = true, FullPath = dir });
                }
                foreach (string file in Directory.GetFiles(_currentLocalPath))
                {
                    _clientItems.Add(new FileItem { Name = Path.GetFileName(file), Icon = "📄", IsFolder = false, FullPath = file });
                }
                UpdateClientPath();
                Log($"Loaded {_clientItems.Count} local items.");
            }
            catch (Exception ex)
            {
                Log("Error loading local items: " + ex.Message);
            }
        }

        private async void lstServerItems_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstServerItems.SelectedItem is FileItem selectedItem)
            {
                if (selectedItem.IsFolder) await ChangeServerDirectory(selectedItem.Name);
                else await DownloadFileAsync(selectedItem.Name);
            }
        }

        private async void lstClientItems_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstClientItems.SelectedItem is FileItem selectedItem)
            {
                if (selectedItem.IsFolder)
                {
                    if (selectedItem.Name == "..")
                    {
                        DirectoryInfo parent = Directory.GetParent(_currentLocalPath);
                        if (parent != null)
                        {
                            _currentLocalPath = parent.FullName;
                            LoadLocalItems();
                        }
                    }
                    else
                    {
                        _currentLocalPath = Path.Combine(_currentLocalPath, selectedItem.Name);
                        LoadLocalItems();
                    }
                }
                else
                {
                    await UploadFileAsync(selectedItem.FullPath);
                }
            }
        }
        private async Task ChangeServerDirectory(string folder)
        {
            if (!CheckConnection()) return;
            try
            {
                await Task.Run(async () =>
                {
                    await _streamSemaphore.WaitAsync();
                    try
                    {
                        if (!CheckConnection()) return;
                        _writer.WriteLine("CD " + folder);
                        await _writer.FlushAsync();
                    }
                    finally
                    {
                        _streamSemaphore.Release();
                    }

                    string response = await ReadLineWithTimeoutAsync();
                    Log("Server: " + response);

                    if (response?.StartsWith("OK:") == true)
                    {
                        _currentServerPath = response.Substring("OK:".Length).Trim();
                        await Dispatcher.InvokeAsync(() => {
                            btnGoUp.IsEnabled = true; // Kích hoạt nút Go Up sau khi chuyển thư mục thành công
                            LoadServerItemsAsync();
                        });
                    }
                    else
                    {
                        Log("Cannot change directory: " + response);
                    }
                });
            }
            catch (Exception ex)
            {
                Log("Error changing directory: " + ex.Message);
            }
        }

        private async void btnGoUp_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            btnGoUp.IsEnabled = false; // Vô hiệu hóa nút trong quá trình xử lý
            try
            {
                Log("Going up to parent directory...");
                await Task.Run(async () =>
                {
                    await _streamSemaphore.WaitAsync();
                    try
                    {
                        if (!CheckConnection()) return;
                        _writer.WriteLine("CDUP");
                        await _writer.FlushAsync();
                    }
                    finally
                    {
                        _streamSemaphore.Release();
                    }

                    string response = await ReadLineWithTimeoutAsync();
                    Log("Server: " + response);

                    if (response?.StartsWith("OK:") == true)
                    {
                        _currentServerPath = response.Substring("OK:".Length).Trim();
                        await Dispatcher.InvokeAsync(async () => {
                            // Cập nhật đường dẫn hiện tại trong UI
                            txtCurrentServerPath.Text = _currentServerPath;
                            await LoadServerItemsAsync();

                            // Kích hoạt lại nút chỉ khi chưa phải thư mục gốc
                            btnGoUp.IsEnabled = !string.IsNullOrEmpty(_currentServerPath) && _currentServerPath != "/";
                        });
                    }
                    else
                    {
                        Log("Error going up: " + (response ?? "No response"));
                        await Dispatcher.InvokeAsync(() => btnGoUp.IsEnabled = true);
                    }
                });
            }
            catch (Exception ex)
            {
                Log("Error going up: " + ex.Message);
                btnGoUp.IsEnabled = true; // Đảm bảo nút được kích hoạt lại trong trường hợp lỗi
            }
        }

        private async void btnCreateFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;
            var inputDialog = new InputDialog("Create Folder", "Enter folder name:");
            if (inputDialog.ShowDialog() == true)
            {
                string folderName = inputDialog.Answer;
                if (!string.IsNullOrWhiteSpace(folderName))
                {
                    try
                    {
                        await Task.Run(async () =>
                        {
                            await _streamSemaphore.WaitAsync();
                            try
                            {
                                if (!CheckConnection()) return;
                                _writer.WriteLine("MKDIR " + folderName);
                                await _writer.FlushAsync();
                            }
                            finally
                            {
                                _streamSemaphore.Release();
                            }
                            string response = await ReadLineWithTimeoutAsync();
                            Log("Server: " + response);
                        });
                        if (_isConnected) await LoadServerItemsAsync();
                    }
                    catch (Exception ex)
                    {
                        Log("Error creating folder: " + ex.Message);
                    }
                }
            }
        }
        #endregion

        #region File Operations
        private async void btnDownload_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            if (lstServerItems.SelectedItem is FileItem selectedItem)
            {
                // Hiển thị thông báo xác nhận với thông tin thư mục đích
                string targetPath = _currentLocalPath;
                string message = selectedItem.IsFolder
                    ? $"Tải thư mục '{selectedItem.Name}' từ server về thư mục local:\n{targetPath}"
                    : $"Tải file '{selectedItem.Name}' từ server về thư mục local:\n{targetPath}";

                var result = MessageBox.Show(message, "Xác nhận tải về", MessageBoxButton.OKCancel, MessageBoxImage.Question);

                if (result == MessageBoxResult.OK)
                {
                    btnDownload.IsEnabled = false; // Vô hiệu hóa nút trong quá trình download

                    try
                    {
                        Log($"Bắt đầu tải {(selectedItem.IsFolder ? "thư mục" : "file")} '{selectedItem.Name}' về {targetPath}");

                        if (selectedItem.IsFolder)
                            await DownloadFolderAsync(selectedItem.Name);
                        else
                            await DownloadFileAsync(selectedItem.Name);

                        Log($"Hoàn thành tải {(selectedItem.IsFolder ? "thư mục" : "file")} '{selectedItem.Name}'");
                    }
                    catch (Exception ex)
                    {
                        Log($"Lỗi khi tải: {ex.Message}");
                    }
                    finally
                    {
                        btnDownload.IsEnabled = true; // Kích hoạt lại nút sau khi hoàn thành
                    }
                }
            }
            else
            {
                // Thông báo nếu không có item nào được chọn
                Log("Vui lòng chọn file hoặc thư mục để tải về");
            }
        }

        private async void btnUpload_Click(object sender, RoutedEventArgs e)
        {
            if (!CheckConnection()) return;

            if (lstClientItems.SelectedItem is FileItem selectedItem && selectedItem.Name != "..")
            {
                btnUpload.IsEnabled = false; // Vô hiệu hóa nút trong quá trình upload

                try
                {
                    if (selectedItem.IsFolder)
                        await UploadFolderAsync(selectedItem.FullPath);
                    else
                        await UploadFileAsync(selectedItem.FullPath);
                }
                finally
                {
                    btnUpload.IsEnabled = true; // Kích hoạt lại nút sau khi hoàn thành
                }
            }
            else
            {
                // Thông báo nếu không có item nào được chọn
                Log("Please select a file or folder to upload");
            }
        }

        private async void btnDeleteServer_Click(object sender, RoutedEventArgs e)
        {
            if (lstServerItems.SelectedItem is FileItem selectedItem)
            {
                var result = MessageBox.Show($"Bạn có chắc chắn muốn xóa '{selectedItem.Name}' khỏi server?", "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    await DeleteServerItemAsync(selectedItem.Name, selectedItem.IsFolder);
                }
            }
        }

        private void btnDeleteClient_Click(object sender, RoutedEventArgs e)
        {
            if (lstClientItems.SelectedItem is FileItem selectedItem && selectedItem.Name != "..")
            {
                var result = MessageBox.Show($"Bạn có chắc chắn muốn xóa '{selectedItem.Name}' trên máy local?", "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    DeleteLocalItem(selectedItem.FullPath, selectedItem.IsFolder);
                }
            }
        }
        #endregion

        #region Async Transfer Logic
        private async Task DownloadFileAsync(string fileName)
        {
            if (!CheckConnection()) return;

            string targetFilePath = Path.Combine(_currentLocalPath, fileName);
            Log($"Tải file: {fileName} về {targetFilePath}");

            // Kiểm tra xem file đã tồn tại hay chưa
            if (File.Exists(targetFilePath))
            {
                var overwriteResult = MessageBox.Show(
                    $"File '{fileName}' đã tồn tại trong thư mục đích. Bạn có muốn ghi đè không?",
                    "File đã tồn tại",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (overwriteResult == MessageBoxResult.No)
                {
                    Log($"Đã hủy tải file '{fileName}'");
                    return;
                }
            }

            const int maxRetries = 3;
            int retryCount = 0;
            FileStream fs = null;

            while (retryCount < maxRetries)
            {
                try
                {
                    Log($"Đang tải file: {fileName} (Lần thử {retryCount + 1}/{maxRetries})");
                    await Task.Run(async () =>
                    {
                        await _streamSemaphore.WaitAsync();
                        try
                        {
                            if (!CheckConnection()) return;
                            _writer.WriteLine("GET " + fileName);
                            await _writer.FlushAsync();
                        }
                        finally
                        {
                            _streamSemaphore.Release();
                        }

                        string response = await ReadLineWithTimeoutAsync(180000);
                        if (response == "SENDING_FILE")
                        {
                            long fileSize = long.Parse(await ReadLineWithTimeoutAsync(180000));
                            Log($"Kích thước file: {fileSize} bytes, bắt đầu tải...");

                            DateTime startTime = DateTime.Now;

                            try
                            {
                                using (fs = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    byte[] buffer = new byte[16384];
                                    long totalBytes = 0;
                                    DateTime lastLogTime = DateTime.Now;

                                    while (totalBytes < fileSize)
                                    {
                                        int bytesToRead = (int)Math.Min(buffer.Length, fileSize - totalBytes);
                                        int bytesRead = await WithTimeout(
                                            _networkStream.ReadAsync(buffer, 0, bytesToRead),
                                            120000,
                                            "Timeout during file download");

                                        if (bytesRead == 0)
                                        {
                                            Log("Server closed connection during download");
                                            break;
                                        }

                                        await fs.WriteAsync(buffer, 0, bytesRead);
                                        totalBytes += bytesRead;

                                        // Log tiến độ mỗi 2 giây
                                        if (DateTime.Now - lastLogTime > TimeSpan.FromSeconds(2))
                                        {
                                            double progress = (double)totalBytes / fileSize * 100;
                                            double elapsedSec = (DateTime.Now - startTime).TotalSeconds;
                                            double speedKBps = elapsedSec > 0 ? (totalBytes / 1024) / elapsedSec : 0;

                                            Log($"Đã tải: {progress:F1}% ({totalBytes}/{fileSize} bytes, {speedKBps:F1} KB/s)");
                                            lastLogTime = DateTime.Now;
                                        }
                                    }

                                    // Đảm bảo ghi tất cả dữ liệu ra đĩa
                                    await fs.FlushAsync();
                                }
                                fs = null;

                                // Tính toán tốc độ download tổng thể
                                TimeSpan downloadTime = DateTime.Now - startTime;
                                double avgSpeedKBps = downloadTime.TotalSeconds > 0
                                    ? (fileSize / 1024) / downloadTime.TotalSeconds
                                    : 0;

                                // Đọc xác nhận từ server
                                string endMarker = await ReadLineWithTimeoutAsync(60000);

                                if (endMarker != "END_OF_FILE")
                                {
                                    Log($"Cảnh báo: Mong đợi END_OF_FILE nhưng nhận được: {endMarker ?? "null"}");
                                }

                                Log($"Đã tải xong file: {fileName} ({fileSize} bytes, {avgSpeedKBps:F1} KB/s)");

                                // Kiểm tra kết nối sau khi download
                                await KeepAliveCheckAsync();

                                // Đợi trước khi làm mới danh sách
                                await Task.Delay(300);
                                Dispatcher.Invoke(() => LoadLocalItems());
                                return; // Thành công, thoát khỏi vòng lặp retry
                            }
                            catch (Exception ex)
                            {
                                Log($"Lỗi khi ghi file: {ex.Message}");
                                if (fs != null)
                                {
                                    fs.Dispose();
                                    fs = null;
                                }
                                throw;
                            }
                        }
                        else
                        {
                            Log("Lỗi server: " + response);
                            throw new Exception("Server phản hồi lỗi: " + response);
                        }
                    });
                    return; // Thành công
                }
                catch (TimeoutException ex)
                {
                    retryCount++;
                    Log($"Timeout khi tải (lần thử {retryCount}/{maxRetries}): {ex.Message}");

                    if (retryCount < maxRetries)
                    {
                        Log("Đang kết nối lại và thử lại...");
                        await ReconnectAsync();
                    }
                    else
                    {
                        Log("Đã đạt số lần thử tối đa. Tải thất bại.");
                        CleanupConnection();
                    }
                }
                catch (Exception ex)
                {
                    Log("Lỗi khi tải: " + ex.Message);

                    // Thử phục hồi kết nối thay vì đóng hoàn toàn
                    bool recovered = await TryRecoverConnectionAsync();
                    if (!recovered)
                    {
                        CleanupConnection();
                    }
                    break;
                }
                finally
                {
                    if (fs != null)
                    {
                        fs.Dispose();
                        fs = null;
                    }
                }
            }
        }

        // Thêm phương thức KeepAliveCheck để đảm bảo kết nối vẫn hoạt động
        private async Task KeepAliveCheckAsync()
        {
            try
            {
                // Kiểm tra kết nối bằng một lệnh nhỏ và đơn giản
                Log("Performing keep-alive check...");
                await _streamSemaphore.WaitAsync();
                try
                {
                    _writer.WriteLine("NOOP"); // No Operation - lệnh giữ kết nối
                    await _writer.FlushAsync();
                }
                finally
                {
                    _streamSemaphore.Release();
                }

                // Đọc phản hồi với timeout ngắn
                string response = await ReadLineWithTimeoutAsync(5000);
                if (response == null)
                {
                    Log("Keep-alive check failed - no response");
                    throw new Exception("Connection may be lost");
                }
                else
                {
                    Log($"Keep-alive response: {response}");
                }
            }
            catch (Exception ex)
            {
                Log($"Keep-alive check failed: {ex.Message}");
                throw; // Rethrow để caller xử lý
            }
        }

        // Thêm phương thức tái kết nối 
        private async Task<bool> ReconnectAsync()
        {
            CleanupConnection();

            try
            {
                Log("Attempting to reconnect...");
                await Task.Delay(1000); // Đợi 1 giây

                string serverIP = txtServerIP.Text.Trim();
                if (string.IsNullOrEmpty(serverIP)) serverIP = "127.0.0.1";

                _client = await Task.Run(() => CreateTcpClientWithTimeout(serverIP, 2121, 10000));
                _networkStream = _client.GetStream();
                _reader = new StreamReader(_networkStream, Encoding.UTF8);
                _writer = new StreamWriter(_networkStream, Encoding.UTF8) { AutoFlush = true };

                string welcome = await ReadLineWithTimeoutAsync(10000);
                Log("Reconnected: " + welcome);

                _isConnected = true;
                Dispatcher.Invoke(() => {
                    btnRefresh.IsEnabled = true;
                    btnGoUp.IsEnabled = true;
                });

                // Khôi phục thư mục hiện tại nếu có
                if (!string.IsNullOrEmpty(_currentServerPath) && _currentServerPath != "/")
                {
                    await NavigateToServerPath(_currentServerPath);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log("Reconnect failed: " + ex.Message);
                return false;
            }
        }

        // Phương thức để đi đến đường dẫn cụ thể trên server
        private async Task NavigateToServerPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return;

            // Tách đường dẫn thành các phần
            string[] parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                await _streamSemaphore.WaitAsync();
                try
                {
                    _writer.WriteLine("CD " + part);
                    await _writer.FlushAsync();
                }
                finally
                {
                    _streamSemaphore.Release();
                }

                string response = await ReadLineWithTimeoutAsync(10000);
                if (!response?.StartsWith("OK:") == true)
                {
                    Log($"Failed to navigate to path: {response}");
                    break;
                }
            }

            Log("Successfully navigated to previous path: " + path);
        }

        // Thêm phương thức thử phục hồi kết nối
        private async Task<bool> TryRecoverConnectionAsync()
        {
            try
            {
                Log("Attempting to recover connection...");

                // Kiểm tra kết nối socket
                if (_client == null || !_client.Connected)
                {
                    return await ReconnectAsync();
                }

                // Kiểm tra stream
                if (_networkStream == null || _reader == null || _writer == null)
                {
                    return await ReconnectAsync();
                }

                // Thử gửi một lệnh đơn giản
                await _streamSemaphore.WaitAsync();
                try
                {
                    _writer.WriteLine("LIST");
                    await _writer.FlushAsync();
                }
                catch (Exception)
                {
                    _streamSemaphore.Release();
                    return await ReconnectAsync();
                }
                _streamSemaphore.Release();

                // Đọc phản hồi
                string response = await ReadLineWithTimeoutAsync(5000);
                if (response == null)
                {
                    return await ReconnectAsync();
                }

                // Đọc và bỏ qua các phản hồi LIST
                while (response != null && response != "END_OF_LIST")
                {
                    response = await ReadLineWithTimeoutAsync(5000);
                }

                Log("Connection recovery successful");
                return true;
            }
            catch (Exception ex)
            {
                Log("Connection recovery failed: " + ex.Message);
                return false;
            }
        }

        private async Task DownloadFolderAsync(string folderName)
        {
            if (!CheckConnection()) return;

            try
            {
                string localFolderPath = Path.Combine(_currentLocalPath, folderName);

                // Kiểm tra xem thư mục đã tồn tại chưa
                if (Directory.Exists(localFolderPath))
                {
                    var overwriteResult = MessageBox.Show(
                        $"Thư mục '{folderName}' đã tồn tại trong thư mục đích. Bạn có muốn ghi đè không?",
                        "Thư mục đã tồn tại",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (overwriteResult == MessageBoxResult.No)
                    {
                        Log($"Đã hủy tải thư mục '{folderName}'");
                        return;
                    }
                }

                // Tạo thư mục đích nếu chưa tồn tại
                Directory.CreateDirectory(localFolderPath);
                Log($"Tải thư mục: {folderName} về {localFolderPath}");

                await Task.Run(async () =>
                {
                    await _streamSemaphore.WaitAsync();
                    try
                    {
                        if (!CheckConnection()) return;
                        _writer.WriteLine("GETDIR " + folderName);
                        await _writer.FlushAsync();
                    }
                    finally
                    {
                        _streamSemaphore.Release();
                    }

                    string response = await ReadLineWithTimeoutAsync(60000);
                    Log("Phản hồi server: " + response);

                    if (response == "SENDING_DIR")
                    {
                        int fileCount = int.Parse(await ReadLineWithTimeoutAsync(60000));
                        Log($"Server sẽ gửi {fileCount} files");

                        int successCount = 0;
                        long totalDownloadedBytes = 0;
                        DateTime startTime = DateTime.Now;

                        for (int i = 0; i < fileCount; i++)
                        {
                            string relativePath = await ReadLineWithTimeoutAsync(60000);
                            long fileSize = long.Parse(await ReadLineWithTimeoutAsync(60000));

                            string targetFilePath = Path.Combine(localFolderPath, relativePath);
                            string parentDir = Path.GetDirectoryName(targetFilePath);

                            Log($"Đang nhận file {i + 1}/{fileCount}: {relativePath} ({fileSize} bytes)");

                            if (!string.IsNullOrEmpty(parentDir))
                                Directory.CreateDirectory(parentDir);

                            // Tải từng file trong thư mục
                            FileStream fs = null;
                            try
                            {
                                fs = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);

                                byte[] buffer = new byte[8192];
                                long totalBytes = 0;
                                DateTime lastProgressUpdate = DateTime.Now;

                                while (totalBytes < fileSize)
                                {
                                    int bytesToRead = (int)Math.Min(buffer.Length, fileSize - totalBytes);
                                    int bytesRead = await WithTimeout(
                                        _networkStream.ReadAsync(buffer, 0, bytesToRead),
                                        60000,
                                        "Timeout during folder download");

                                    if (bytesRead == 0) break;

                                    await fs.WriteAsync(buffer, 0, bytesRead);
                                    totalBytes += bytesRead;
                                    totalDownloadedBytes += bytesRead;

                                    // Hiển thị tiến độ cho file lớn mỗi 2 giây
                                    if (fileSize > 1024 * 1024 && DateTime.Now - lastProgressUpdate > TimeSpan.FromSeconds(2))
                                    {
                                        double progress = (double)totalBytes / fileSize * 100;
                                        await Dispatcher.InvokeAsync(() =>
                                            Log($"File {i + 1}/{fileCount}: {progress:F1}% ({totalBytes}/{fileSize} bytes)"));
                                        lastProgressUpdate = DateTime.Now;
                                    }
                                }

                                // Đảm bảo dữ liệu được ghi đầy đủ
                                await fs.FlushAsync();
                                fs.Close();
                                fs = null;

                                successCount++;
                            }
                            catch (Exception ex)
                            {
                                Log($"Lỗi khi tải file {relativePath}: {ex.Message}");
                                if (fs != null)
                                {
                                    fs.Dispose();
                                    fs = null;
                                }
                            }
                        }

                        // Tính toán tốc độ download
                        TimeSpan downloadTime = DateTime.Now - startTime;
                        double speedKBps = downloadTime.TotalSeconds > 0
                            ? totalDownloadedBytes / 1024 / downloadTime.TotalSeconds
                            : 0;

                        string endMsg = await ReadLineWithTimeoutAsync(60000);
                        Log("Phản hồi server: " + endMsg);
                        Log($"Đã tải thư mục '{folderName}' thành công: {successCount}/{fileCount} files " +
                            $"({totalDownloadedBytes / 1024:N0} KB, tốc độ: {speedKBps:N1} KB/s)");

                        // Kiểm tra kết nối sau khi tải xong
                        await KeepAliveCheckAsync();

                        // Cập nhật danh sách file local
                        await Dispatcher.InvokeAsync(() => LoadLocalItems());
                    }
                    else
                    {
                        Log("Lỗi khi tải thư mục: " + response);
                    }
                });
            }
            catch (Exception ex)
            {
                Log("Lỗi tải thư mục: " + ex.Message);

                // Thử phục hồi kết nối thay vì đóng hoàn toàn
                bool recovered = await TryRecoverConnectionAsync();
                if (!recovered)
                {
                    CleanupConnection();
                }
            }
        }

        private async Task UploadFileAsync(string filePath)
        {
            if (!CheckConnection() || !File.Exists(filePath)) return;

            _isUploading = true;
            try
            {
                string fileName = Path.GetFileName(filePath);
                Log($"Uploading file: {fileName} to server path: {_currentServerPath}");

                await Task.Run(async () =>
                {
                    // Gửi lệnh PUT, nhận phản hồi từ server
                    await _streamSemaphore.WaitAsync();
                    try
                    {
                        if (!CheckConnection()) return;
                        _writer.WriteLine("PUT " + fileName);
                        await _writer.FlushAsync();
                        Log("Sent PUT command to server.");
                    }
                    finally
                    {
                        _streamSemaphore.Release();
                    }

                    string serverResponse = await ReadLineWithTimeoutAsync(30000); // Tăng timeout lên 30s
                    Log("Server response: " + (serverResponse ?? "No response"));

                    if (serverResponse?.StartsWith("SENDING_FILE") == true)
                    {
                        // Gửi thông tin file và dữ liệu file
                        await _streamSemaphore.WaitAsync();
                        try
                        {
                            _writer.WriteLine("SENDING_FILE");
                            FileInfo fi = new FileInfo(filePath);
                            _writer.WriteLine(fi.Length);
                            await _writer.FlushAsync();
                            Log($"Sent file info to server: {fileName}, {fi.Length} bytes");
                        }
                        finally
                        {
                            _streamSemaphore.Release();
                        }

                        // Gửi dữ liệu file
                        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                        {
                            byte[] buffer = new byte[8192];
                            int bytesRead;
                            long totalSent = 0;
                            long fileSize = new FileInfo(filePath).Length;
                            DateTime lastLogTime = DateTime.Now;

                            while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await _streamSemaphore.WaitAsync();
                                try
                                {
                                    await _networkStream.WriteAsync(buffer, 0, bytesRead);
                                    await _networkStream.FlushAsync();
                                }
                                finally
                                {
                                    _streamSemaphore.Release();
                                }

                                totalSent += bytesRead;

                                // Hiển thị tiến độ mỗi 2 giây
                                if (DateTime.Now - lastLogTime > TimeSpan.FromSeconds(2))
                                {
                                    double progress = (double)totalSent / fileSize * 100;
                                    Log($"Upload progress: {progress:F1}% ({totalSent}/{fileSize} bytes)");
                                    lastLogTime = DateTime.Now;
                                }
                            }

                            Log("File data sent completely.");
                        }

                        // Đọc xác nhận từ server
                        string result = null;
                        try
                        {
                            result = await ReadLineWithTimeoutAsync(30000);
                            Log($"Server confirmation: {result ?? "No confirmation received"}");
                        }
                        catch (Exception readEx)
                        {
                            Log($"Error reading server confirmation: {readEx.Message}");
                        }

                        // Kiểm tra kết nối sau khi upload
                        await KeepAliveCheckAsync();

                        // Làm mới danh sách server sau khi upload
                        if (_isConnected)
                        {
                            await Task.Delay(500); // Đợi server xử lý xong
                            await LoadServerItemsAsync();
                            Log("Server items refreshed after upload.");
                        }
                    }
                    else
                    {
                        Log("Server error: " + (serverResponse ?? "No response"));
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    Log("Upload process error: " + ex.Message);
                    // Thử phục hồi kết nối thay vì đóng hoàn toàn
                    _ = TryRecoverConnectionAsync();
                });
            }
            finally
            {
                _isUploading = false;
            }
        }
        private async Task UploadFolderAsync(string folderPath)
        {
            if (!CheckConnection() || !Directory.Exists(folderPath)) return;

            _isUploading = true;
            try
            {
                string folderName = Path.GetFileName(folderPath);
                Log($"Uploading folder: {folderName} to server path: {_currentServerPath}");

                // Đếm số lượng file và kích thước tổng để ước lượng thời gian
                var allFiles = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                long totalSize = allFiles.Sum(f => new FileInfo(f).Length);
                Log($"Found {allFiles.Length} files, total size: {totalSize} bytes");

                await Task.Run(async () =>
                {
                    // Bước 1: Gửi lệnh PUTDIR
                    await _streamSemaphore.WaitAsync();
                    try
                    {
                        if (!CheckConnection()) return;
                        _writer.WriteLine("PUTDIR " + folderName);
                        await _writer.FlushAsync();
                        Log("Sent PUTDIR command to server.");
                    }
                    finally
                    {
                        _streamSemaphore.Release();
                    }

                    // Bước 2: Nhận phản hồi từ server
                    string response = await ReadLineWithTimeoutAsync(30000);
                    Log("Server response: " + (response ?? "No response"));

                    if (response == "READY_FOR_DIR")
                    {
                        // Bước 3: Gửi thông tin về thư mục
                        await _streamSemaphore.WaitAsync();
                        try
                        {
                            _writer.WriteLine("SENDING_DIR");
                            _writer.WriteLine(allFiles.Length.ToString());
                            await _writer.FlushAsync();
                            Log($"Sent folder info to server: {allFiles.Length} files");
                        }
                        finally
                        {
                            _streamSemaphore.Release();
                        }

                        // Bước 4: Gửi từng file trong thư mục
                        int fileCounter = 0;
                        long uploadedBytes = 0;
                        DateTime lastProgressUpdate = DateTime.Now;

                        foreach (var file in allFiles)
                        {
                            fileCounter++;
                            string relativePath = file.Substring(folderPath.Length + 1);
                            FileInfo fi = new FileInfo(file);

                            await _streamSemaphore.WaitAsync();
                            try
                            {
                                _writer.WriteLine(relativePath);
                                _writer.WriteLine(fi.Length.ToString());
                                await _writer.FlushAsync();
                                Log($"Sending file ({fileCounter}/{allFiles.Length}): {relativePath} ({fi.Length} bytes)");
                            }
                            finally
                            {
                                _streamSemaphore.Release();
                            }

                            // Gửi dữ liệu file
                            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                byte[] buffer = new byte[4096];
                                int bytesRead;
                                long fileBytesUploaded = 0;

                                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await _streamSemaphore.WaitAsync();
                                    try
                                    {
                                        await _networkStream.WriteAsync(buffer, 0, bytesRead);
                                        await _networkStream.FlushAsync();

                                        fileBytesUploaded += bytesRead;
                                        uploadedBytes += bytesRead;
                                    }
                                    finally
                                    {
                                        _streamSemaphore.Release();
                                    }

                                    // Hiển thị tiến độ mỗi 2 giây
                                    if (DateTime.Now - lastProgressUpdate > TimeSpan.FromSeconds(2))
                                    {
                                        double progress = totalSize > 0 ? (double)uploadedBytes / totalSize * 100 : 0;
                                        await Dispatcher.InvokeAsync(() =>
                                            Log($"Upload progress: {progress:F1}% ({uploadedBytes}/{totalSize} bytes)"));
                                        lastProgressUpdate = DateTime.Now;
                                    }

                                    // Tạm dừng ngắn giữa các block lớn
                                    if (fileBytesUploaded % (1024 * 1024) < buffer.Length)
                                        await Task.Delay(1);
                                }
                            }

                            // Đợi một chút giữa các file để tránh quá tải
                            if (fileCounter % 10 == 0)
                                await Task.Delay(100);
                        }

                        // Bước 5: Đợi xác nhận từ server
                        string doneResp = await ReadLineWithTimeoutAsync(60000);
                        Log("Server: " + doneResp);

                        // Kiểm tra kết nối sau khi upload
                        await KeepAliveCheckAsync();
                    }
                    else
                    {
                        Log("Server not ready for folder: " + response);
                    }
                });

                // Đợi và làm mới danh sách server
                await Task.Delay(1000);
                if (_isConnected)
                {
                    Log("Refreshing server items after folder upload...");
                    await LoadServerItemsAsync();
                    Log("Server file list refreshed successfully");
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    Log("Upload folder error: " + ex.Message);
                    // Thử phục hồi kết nối thay vì đóng hoàn toàn
                    _ = TryRecoverConnectionAsync();
                });
            }
            finally
            {
                _isUploading = false;
            }
        }

        // Thêm phương thức mới này để kiểm tra và phục hồi kết nối
        private async Task RecoverConnectionAsync()
        {
            try
            {
                // Gửi lệnh LIST đơn giản để kiểm tra kết nối
                await _streamSemaphore.WaitAsync();
                try
                {
                    _writer.WriteLine("LIST");
                    await _writer.FlushAsync();
                    Log("Testing connection with LIST command...");
                }
                finally
                {
                    _streamSemaphore.Release();
                }

                // Đọc phản hồi để xác nhận kết nối vẫn hoạt động
                string response = await ReadLineWithTimeoutAsync(10000);
                if (response != null)
                {
                    Log("Connection recovery successful: " + response);
                    await LoadServerItemsAsync();
                }
                else
                {
                    Log("No response from server, connection may be lost");
                    CleanupConnection();
                }
            }
            catch (Exception ex)
            {
                Log("Connection recovery failed: " + ex.Message);
                CleanupConnection();
            }
        }

        private async Task DeleteServerItemAsync(string itemName, bool isFolder)
        {
            if (!CheckConnection()) return;
            try
            {
                string command = isFolder ? "RMDIR " : "DELETE ";
                Log($"Deleting server {(isFolder ? "folder" : "file")}: {itemName}");

                await Task.Run(async () =>
                {
                    await _streamSemaphore.WaitAsync();
                    try
                    {
                        if (!CheckConnection()) return;
                        _writer.WriteLine(command + itemName);
                        await _writer.FlushAsync();
                    }
                    finally
                    {
                        _streamSemaphore.Release();
                    }
                    string response = await ReadLineWithTimeoutAsync();
                    Log("Server: " + response);
                });

                if (_isConnected) await LoadServerItemsAsync();
            }
            catch (Exception ex)
            {
                Log("Delete server item error: " + ex.Message);
                CleanupConnection();
            }
        }

        private void DeleteLocalItem(string itemPath, bool isFolder)
        {
            try
            {
                if (isFolder)
                {
                    // Xóa thư mục đệ quy
                    Directory.Delete(itemPath, true);
                }
                else
                {
                    // Đối với file, thử giải phóng tài nguyên trước khi xóa
                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    // Nếu file đang được sử dụng, hãy thử đặt thuộc tính Normal trước
                    File.SetAttributes(itemPath, FileAttributes.Normal);

                    // Thử xóa file với retry
                    int retryCount = 0;
                    bool deleted = false;
                    Exception lastException = null;

                    while (retryCount < 3 && !deleted)
                    {
                        try
                        {
                            File.Delete(itemPath);
                            deleted = true;
                        }
                        catch (IOException ioEx)
                        {
                            lastException = ioEx;
                            retryCount++;
                            // Đợi một chút trước khi thử lại
                            Thread.Sleep(500);
                        }
                        catch (UnauthorizedAccessException uaEx)
                        {
                            lastException = uaEx;
                            retryCount++;
                            Thread.Sleep(500);
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            retryCount++;
                            Thread.Sleep(500);
                        }
                    }

                    if (!deleted)
                    {
                        throw lastException ?? new Exception("Không thể xóa file sau nhiều lần thử");
                    }
                }

                Log($"Deleted local {(isFolder ? "folder" : "file")}: {itemPath}");
                LoadLocalItems();
            }
            catch (Exception ex)
            {
                Log($"Delete local item error: {ex.Message}");
                MessageBox.Show($"Không thể xóa: {ex.Message}\n\nLý do có thể là file đang được sử dụng bởi một ứng dụng khác hoặc bạn không có quyền xóa.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region Housekeeping
        private bool CheckConnection()
        {
            if (!_isConnected)
            {
                Log("Not connected to server!");
                Dispatcher.Invoke(() => { btnRefresh.IsEnabled = false; });
                return false;
            }

            try
            {
                // Kiểm tra kết nối socket
                if (_client == null || !_client.Connected)
                {
                    Log("TCP client is not connected!");
                    CleanupConnection();
                    return false;
                }

                // Kiểm tra stream tồn tại
                if (_networkStream == null || _reader == null || _writer == null)
                {
                    Log("Network streams are not available!");
                    CleanupConnection();
                    return false;
                }

                // Kiểm tra khả năng đọc/ghi (không chặn)
                if (_client.Client != null)
                {
                    if (!_client.Client.Poll(0, SelectMode.SelectRead) ||
                        !_client.Client.Poll(0, SelectMode.SelectWrite))
                    {
                        // Socket không sẵn sàng để đọc/ghi
                        if (_client.Client.Poll(0, SelectMode.SelectError))
                        {
                            Log("Socket error detected!");
                            CleanupConnection();
                            return false;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Connection check error: {ex.Message}");
                CleanupConnection();
                Dispatcher.Invoke(() => { btnRefresh.IsEnabled = false; });
                return false;
            }
        }

        private void CleanupConnection()
        {
            _isUploading = false;
            _isConnected = false;

            _reader?.Close();
            _writer?.Close();
            _networkStream?.Close();
            _client?.Close();

            _client = null;
            _networkStream = null;
            _reader = null;
            _writer = null;

            Dispatcher.Invoke(() =>
            {
                btnRefresh.IsEnabled = false;
                btnGoUp.IsEnabled = false;
                btnCreateFolder.IsEnabled = false;
                Log("Connection closed.");
            });
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText($"{DateTime.Now:HH:mm:ss} - {message}\n");
                txtLog.ScrollToEnd();
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            CleanupConnection();
            base.OnClosed(e);
        }
        #endregion
    }

    public partial class InputDialog : Window
    {
        public string Answer => txtInput.Text;
        private TextBox txtInput;

        public InputDialog(string title, string message, string defaultValue = "")
        {
            InitializeComponent(title, message, defaultValue);
        }

        private void InitializeComponent(string title, string message, string defaultValue)
        {
            Title = title;
            Width = 350;
            Height = 180;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = System.Windows.Media.Brushes.White;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Margin = new Thickness(15);

            var lblMessage = new TextBlock
            {
                Text = message,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 15),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(lblMessage, 0);
            grid.Children.Add(lblMessage);

            txtInput = new TextBox
            {
                Text = defaultValue,
                Height = 25,
                Padding = new Thickness(5),
                Margin = new Thickness(0, 0, 0, 15),
                BorderThickness = new Thickness(1),
                BorderBrush = System.Windows.Media.Brushes.Gray
            };
            Grid.SetRow(txtInput, 1);
            grid.Children.Add(txtInput);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnOK = new Button { Content = "OK", Width = 70, Height = 25, IsDefault = true, Margin = new Thickness(0, 0, 10, 0), Background = System.Windows.Media.Brushes.LightBlue };
            btnOK.Click += (s, e) => { DialogResult = true; Close(); };
            var btnCancel = new Button { Content = "Cancel", Width = 70, Height = 25, IsCancel = true };
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };
            buttonPanel.Children.Add(btnOK);
            buttonPanel.Children.Add(btnCancel);
            Grid.SetRow(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            Content = grid;
            Loaded += (s, e) => txtInput.Focus();
        }
    }
}