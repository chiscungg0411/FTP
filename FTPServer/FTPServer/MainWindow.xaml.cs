using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FTPServerWPF
{
    public partial class MainWindow : Window
    {
        private TcpListener _listener;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isRunning = false;

        private string sharedFolder = @"D:\SharedServer";

        public MainWindow()
        {
            InitializeComponent();
            btnStopServer.IsEnabled = false;
        }

        private void btnStartServer_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning)
            {
                try
                {
                    if (!Directory.Exists(sharedFolder))
                    {
                        Directory.CreateDirectory(sharedFolder);
                    }

                    _cancellationTokenSource = new CancellationTokenSource();
                    _listener = new TcpListener(IPAddress.Any, 2121);
                    _listener.Start();
                    _isRunning = true;

                    Task.Run(() => ListenForClients(_cancellationTokenSource.Token));

                    Log("FTP Server WPF đã khởi động trên cổng 2121...");
                    btnStartServer.IsEnabled = false;
                    btnStopServer.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    Log("Lỗi khởi động: " + ex.Message);
                }
            }
            else
            {
                Log("Server đang chạy rồi!");
            }
        }

        private async Task ListenForClients(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && _listener != null)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        Log("Listener đã bị dispose khi đang chờ kết nối.");
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        Log("Listener đã bị dừng khi đang chờ kết nối.");
                        break;
                    }
                    
                    string endpoint = "Unknown";
                    try
                    {
                        endpoint = client?.Client?.RemoteEndPoint?.ToString() ?? "Unknown";
                    }
                    catch { }

                    Log($"Client kết nối từ: {endpoint}");
                    _ = Task.Run(() => HandleClient(client), token);
                }
            }
            catch (OperationCanceledException)
            {
                Log("Server đã dừng (OperationCanceled).");
            }
            catch (ObjectDisposedException)
            {
                Log("Server listener đã bị disposed.");
            }
            catch (Exception ex)
            {
                Log($"Lỗi ListenForClients: {ex.Message}");
            }
            finally
            {
                _listener?.Stop();
            }
        }

        private void HandleClient(TcpClient client)
        {
            string clientEndpoint = "Unknown";
            try
            {
                clientEndpoint = client?.Client?.RemoteEndPoint?.ToString() ?? "Unknown";
            }
            catch (ObjectDisposedException)
            {
                clientEndpoint = "Client đã bị dispose ngay khi kết nối";
            }
            catch (Exception ex)
            {
                clientEndpoint = $"Lỗi lấy endpoint: {ex.Message}";
            }

            try
            {
                using NetworkStream ns = client.GetStream();
                using StreamReader reader = new StreamReader(ns, Encoding.UTF8);
                using StreamWriter writer = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };

                writer.WriteLine("Chào mừng đến với FTP Server WPF!");
                Log($"Client {clientEndpoint} đã kết nối thành công.");

                string currentPath = "";

                while (IsClientConnected(client))
                {
                    string command = reader.ReadLine();
                    if (command == null) break;

                    Log($"Nhận lệnh từ {clientEndpoint}: {command}");
                    string[] parts = command.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    
                    string mainCmd = parts.Length > 0 ? parts[0].ToUpper() : string.Empty;
                    string argument = parts.Length > 1 ? parts[1] : string.Empty;

                    switch (mainCmd)
                    {
                        case "LIST":
                            HandleList(writer, currentPath);
                            break;
                        case "CD":
                            currentPath = HandleChangeDirectory(writer, currentPath, argument);
                            break;
                        case "CDUP":
                            currentPath = HandleChangeDirectoryUp(writer, currentPath);
                            break;
                        case "GET":
                            HandleGet(writer, ns, Path.Combine(currentPath, argument));
                            break;
                        case "GETDIR":
                            HandleGetDir(writer, ns, Path.Combine(currentPath, argument));
                            break;
                        case "PUT":
                            HandlePut(reader, writer, ns, Path.Combine(currentPath, argument));
                            break;
                        case "PUTDIR":
                            HandlePutDir(reader, writer, ns, Path.Combine(currentPath, argument));
                            break;
                        case "DELETE":
                            HandleDelete(writer, Path.Combine(currentPath, argument));
                            break;
                        case "RMDIR":
                            HandleDeleteDirectory(writer, Path.Combine(currentPath, argument));
                            break;
                        case "MKDIR":
                            HandleMakeDirectory(writer, Path.Combine(currentPath, argument));
                            break;
                        case "QUIT":
                            writer.WriteLine("Bye");
                            return;
                        case "NOOP":
                            writer.WriteLine("OK");
                            break;
                        default:
                            writer.WriteLine("Lệnh không hợp lệ.");
                            break;
                    }
                }
            }
            catch (IOException ioEx) when (ioEx.InnerException is SocketException)
            {
                Log($"Client {clientEndpoint} đã ngắt kết nối (SocketException).");
            }
            catch (ObjectDisposedException)
            {
                Log($"Client {clientEndpoint} - Socket đã bị disposed khi đang xử lý.");
            }
            catch (Exception ex)
            {
                Log($"Lỗi HandleClient từ {clientEndpoint}: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (client != null && client.Connected)
                    {
                        client.Close();
                        Log($"Đã đóng kết nối với {clientEndpoint}.");
                    }
                    else if (client != null)
                    {
                        Log($"Client {clientEndpoint} đã ngắt kết nối trước khi đóng.");
                    }
                }
                catch (ObjectDisposedException)
                {
                    Log($"Client {clientEndpoint} - Socket đã được dispose trước khi cố gắng đóng.");
                }
                catch (Exception ex)
                {
                    Log($"Lỗi khi đóng kết nối {clientEndpoint}: {ex.Message}");
                }
            }
        }

        private bool IsClientConnected(TcpClient client)
        {
            try
            {
                return client != null && client.Connected && client.Client != null && client.Client.Connected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void HandleList(StreamWriter writer, string path)
        {
            string fullPath = string.IsNullOrEmpty(path) ? sharedFolder : Path.Combine(sharedFolder, path);
            if (!Directory.Exists(fullPath) || !fullPath.StartsWith(Path.GetFullPath(sharedFolder)))
            {
                writer.WriteLine("Đường dẫn không hợp lệ.");
                writer.WriteLine("END_OF_LIST");
                return;
            }
            writer.WriteLine("DANH SÁCH THƯ MỤC VÀ FILE:");
            try
            {
                foreach (var dir in Directory.GetDirectories(fullPath)) writer.WriteLine("[Folder] " + Path.GetFileName(dir));
                foreach (var file in Directory.GetFiles(fullPath)) writer.WriteLine("[File] " + Path.GetFileName(file));
            }
            catch (UnauthorizedAccessException)
            {
                writer.WriteLine("Lỗi: Không có quyền truy cập.");
            }
            catch (Exception ex)
            {
                writer.WriteLine("Lỗi khi đọc thư mục: " + ex.Message);
            }
            writer.WriteLine("END_OF_LIST");
        }

        private string HandleChangeDirectory(StreamWriter writer, string currentPath, string newDir)
        {
            if (string.IsNullOrEmpty(newDir))
            {
                writer.WriteLine("Thiếu tên thư mục.");
                return currentPath;
            }
            string combinedPath = Path.Combine(currentPath, newDir);
            string fullPath = Path.GetFullPath(Path.Combine(sharedFolder, combinedPath));
            
            if (Directory.Exists(fullPath) && fullPath.StartsWith(Path.GetFullPath(sharedFolder)))
            {
                currentPath = fullPath.Substring(Path.GetFullPath(sharedFolder).Length);
                currentPath = currentPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                
                writer.WriteLine("OK:" + (string.IsNullOrEmpty(currentPath) ? "/" : currentPath));
                return currentPath;
            }
            writer.WriteLine("Không thể truy cập thư mục.");
            return currentPath;
        }

        private string HandleChangeDirectoryUp(StreamWriter writer, string currentPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(currentPath))
                {
                    // Lấy thư mục cha
                    string parentPath = Path.GetDirectoryName(currentPath) ?? "";
                    parentPath = parentPath.Replace('\\', '/'); // Chuẩn hóa đường dẫn

                    // Trả về đường dẫn hiện tại sau khi di chuyển lên
                    writer.WriteLine("OK:" + (string.IsNullOrEmpty(parentPath) ? "/" : parentPath));
                    Log($"Change directory up from '{currentPath}' to '{parentPath}'");
                    return parentPath;
                }
                else
                {
                    writer.WriteLine("OK:/");
                    Log("Already at root directory");
                    return "";
                }
            }
            catch (Exception ex)
            {
                writer.WriteLine($"Lỗi khi di chuyển lên thư mục cha: {ex.Message}");
                Log($"Error in CDUP command: {ex.Message}");
                return currentPath; // Giữ nguyên đường dẫn hiện tại trong trường hợp lỗi
            }
        }

        private void HandleGet(StreamWriter writer, NetworkStream ns, string relativePath)
        {
            string filePath = Path.Combine(sharedFolder, relativePath);
            if (!File.Exists(filePath))
            {
                writer.WriteLine("File không tồn tại.");
                return;
            }

            writer.WriteLine("SENDING_FILE");
            FileInfo fi = new FileInfo(filePath);
            writer.WriteLine(fi.Length);
            writer.Flush(); // Đảm bảo header được gửi ngay lập tức

            try
            {
                // Sử dụng buffer size nhỏ hơn để tránh quá tải
                byte[] buffer = new byte[16384]; // 16KB
                long totalSent = 0;

                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    int bytesRead;
                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ns.Write(buffer, 0, bytesRead);
                        ns.Flush(); // Đảm bảo dữ liệu được gửi

                        totalSent += bytesRead;

                        // Đối với file lớn, tạm dừng một chút sau mỗi 1MB để tránh quá tải
                        if (totalSent % (1024 * 1024) < buffer.Length)
                        {
                            Thread.Sleep(1);
                        }
                    }
                }

                // Đảm bảo gửi xong tất cả dữ liệu trước khi gửi kết thúc
                ns.Flush();

                // Tạm dừng ngắn trước khi gửi thông báo kết thúc
                Thread.Sleep(100);

                writer.WriteLine("END_OF_FILE");
                writer.Flush();

                Log($"Đã gửi file thành công: {relativePath} ({fi.Length} bytes)");
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi gửi file {relativePath}: {ex.Message}");
                try
                {
                    writer.WriteLine("ERROR: " + ex.Message);
                    writer.Flush();
                }
                catch { /* Bỏ qua nếu không thể gửi lỗi */ }
            }
        }

        private void HandlePut(StreamReader reader, StreamWriter writer, NetworkStream ns, string relativePath)
        {
            writer.WriteLine("SENDING_FILE");

            string clientResponse = reader.ReadLine();
            if (clientResponse != "SENDING_FILE")
            {
                writer.WriteLine("Lỗi giao thức: Client không xác nhận.");
                return;
            }

            string sizeStr = reader.ReadLine();
            if (!long.TryParse(sizeStr, out long fileSize))
            {
                writer.WriteLine("Kích thước file không hợp lệ.");
                return;
            }

            string filePath = Path.Combine(sharedFolder, relativePath);
            try
            {
                string parentDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                // Khai báo totalBytes ở đây để nó có thể được truy cập sau khối using
                long totalBytes = 0; 
                using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[8192];
                    
                    while (totalBytes < fileSize)
                    {
                        int bytesToRead = (int)Math.Min(buffer.Length, fileSize - totalBytes);
                        int bytesRead = ns.Read(buffer, 0, bytesToRead);
                        if (bytesRead == 0) break;
                        fs.Write(buffer, 0, bytesRead);
                        totalBytes += bytesRead;
                    }
                }
                Log($"Đã nhận file: {relativePath} ({totalBytes} / {fileSize} bytes)");
                writer.WriteLine("Đã nhận file thành công!");
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi nhận file {relativePath}: {ex.Message}");
                writer.WriteLine("Lỗi server khi lưu file.");
            }
        }

        private void HandleDelete(StreamWriter writer, string relativePath)
        {
            string pathToDelete = Path.Combine(sharedFolder, relativePath);
            try
            {
                if (File.Exists(pathToDelete))
                {
                    File.Delete(pathToDelete);
                    writer.WriteLine("File đã được xóa.");
                }
                else
                {
                    writer.WriteLine("File không tồn tại.");
                }
                writer.WriteLine("END_OF_FILE");
            }
            catch (Exception ex)
            {
                Log($"Lỗi xóa file {relativePath}: {ex.Message}");
                writer.WriteLine("Lỗi xóa file: " + ex.Message);
            }
        }

        private void HandleDeleteDirectory(StreamWriter writer, string relativePath)
        {
            string dirPath = Path.Combine(sharedFolder, relativePath);
            string response;
            bool deleted = false;
            int retryCount = 0;

            try
            {
                if (!Directory.Exists(dirPath))
                {
                    writer.WriteLine("Thư mục không tồn tại.");
                    return;
                }

                if (Path.GetFullPath(dirPath).Equals(Path.GetFullPath(sharedFolder), StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteLine("Lỗi: Không thể xóa thư mục gốc của Server.");
                    return;
                }

                while (retryCount < 3 && !deleted)
                {
                    try
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        SetAllFileAttributesToNormal(dirPath);
                        Directory.Delete(dirPath, true);
                        deleted = true;
                        Log($"Đã xóa thư mục thành công: {dirPath}");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        retryCount++;
                        Log($"Lỗi quyền truy cập khi xóa thư mục {relativePath}. Thử lại lần {retryCount}");
                        Thread.Sleep(500);
                    }
                    catch (IOException ioEx)
                    {
                        retryCount++;
                        Log($"Lỗi IO khi xóa thư mục {relativePath}: {ioEx.Message}. Thử lại lần {retryCount}");
                        Thread.Sleep(1000);
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        Log($"Lỗi chung khi xóa thư mục {relativePath}: {ex.Message}. Thử lại lần {retryCount}");
                        Thread.Sleep(500);
                    }
                }

                if (deleted || !Directory.Exists(dirPath))
                {
                    response = "Thư mục đã được xóa thành công.";
                }
                else
                {
                    response = "Không thể xóa thư mục sau 3 lần thử.";
                    Log($"Không thể xóa thư mục {relativePath} sau 3 lần thử.");
                }
            }
            catch (Exception ex)
            {
                response = "Lỗi khi xóa thư mục: " + ex.Message;
                Log($"Lỗi ngoại lệ khi xóa thư mục {relativePath}: {ex}");
            }

            writer.WriteLine(response);
        }

        private void SetAllFileAttributesToNormal(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                return;

            try
            {
                foreach (string file in Directory.GetFiles(directoryPath))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch (Exception ex)
                    {
                        Log($"Không thể đặt thuộc tính cho file {file}: {ex.Message}");
                    }
                }

                foreach (string subDir in Directory.GetDirectories(directoryPath))
                {
                    SetAllFileAttributesToNormal(subDir);
                }

                File.SetAttributes(directoryPath, FileAttributes.Normal);
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi đặt thuộc tính file trong thư mục {directoryPath}: {ex.Message}");
            }
        }

        private void HandleMakeDirectory(StreamWriter writer, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                writer.WriteLine("Thiếu tên thư mục.");
                return;
            }
            string fullPath = Path.Combine(sharedFolder, relativePath);
            try
            {
                Directory.CreateDirectory(fullPath);
                writer.WriteLine("Thư mục đã được tạo.");
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi tạo thư mục {relativePath}: {ex.Message}");
                writer.WriteLine("Lỗi khi tạo thư mục: " + ex.Message);
            }
        }

        private void HandleGetDir(StreamWriter writer, NetworkStream ns, string relativePath)
        {
            string fullPath = Path.Combine(sharedFolder, relativePath);
            if (!Directory.Exists(fullPath))
            {
                writer.WriteLine("Thư mục không tồn tại.");
                return;
            }
            writer.WriteLine("SENDING_DIR");
            try
            {
                var allFiles = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories);
                writer.WriteLine(allFiles.Length);
                foreach (var file in allFiles)
                {
                    string relPath = file.Substring(fullPath.Length + 1);
                    writer.WriteLine(relPath);
                    FileInfo fi = new FileInfo(file);
                    writer.WriteLine(fi.Length);
                    using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                    {
                        fs.CopyTo(ns);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi gửi thư mục {relativePath}: {ex.Message}");
            }
            writer.WriteLine("END_OF_DIR");
        }

        private void HandlePutDir(StreamReader reader, StreamWriter writer, NetworkStream ns, string relativePath)
        {
            Log($"Bắt đầu xử lý lệnh PUTDIR cho: {relativePath}");
            writer.WriteLine("READY_FOR_DIR");
            writer.Flush(); // Đảm bảo phản hồi được gửi ngay lập tức

            string confirmCmd;
            try
            {
                confirmCmd = reader.ReadLine();
                Log($"Nhận lệnh xác nhận từ client: {confirmCmd}");
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi đọc xác nhận PUTDIR: {ex.Message}");
                return;
            }

            if (confirmCmd != "SENDING_DIR")
            {
                writer.WriteLine("Lỗi giao thức: Client không xác nhận gửi thư mục.");
                writer.Flush();
                return;
            }

            string fileCountStr;
            try
            {
                fileCountStr = reader.ReadLine();
                Log($"Số lượng file được báo cáo: {fileCountStr}");
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi đọc số lượng file: {ex.Message}");
                return;
            }

            if (!int.TryParse(fileCountStr, out int fileCount))
            {
                writer.WriteLine("Số lượng file không hợp lệ.");
                writer.Flush();
                return;
            }

            string fullPath = Path.Combine(sharedFolder, relativePath);
            try
            {
                Directory.CreateDirectory(fullPath);
                Log($"Đã tạo thư mục đích: {fullPath}");
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi tạo thư mục {relativePath} trên server: {ex.Message}");
                writer.WriteLine("Lỗi server khi tạo thư mục.");
                writer.Flush();
                return;
            }

            int successCount = 0;
            long totalBytesReceived = 0;
            DateTime startTime = DateTime.Now;
            DateTime lastLogTime = startTime;

            for (int i = 0; i < fileCount; i++)
            {
                string relPath;
                try
                {
                    relPath = reader.ReadLine();
                    Log($"Đang nhận file ({i + 1}/{fileCount}): {relPath}");
                }
                catch (Exception ex)
                {
                    Log($"Lỗi khi đọc đường dẫn file: {ex.Message}");
                    continue;
                }

                string fileSizeStr;
                try
                {
                    fileSizeStr = reader.ReadLine();
                }
                catch (Exception ex)
                {
                    Log($"Lỗi khi đọc kích thước file {relPath}: {ex.Message}");
                    continue;
                }

                if (!long.TryParse(fileSizeStr, out long fileSize))
                {
                    Log($"Kích thước file không hợp lệ: {fileSizeStr}");
                    continue;
                }

                string targetFilePath = Path.Combine(fullPath, relPath);
                string parentDir = Path.GetDirectoryName(targetFilePath);

                try
                {
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    long fileBytesReceived = 0;
                    using (FileStream fs = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buffer = new byte[4096]; // Giảm kích thước buffer xuống 4KB

                        while (fileBytesReceived < fileSize)
                        {
                            int bytesToRead = (int)Math.Min(buffer.Length, fileSize - fileBytesReceived);
                            int bytesRead;

                            try
                            {
                                bytesRead = ns.Read(buffer, 0, bytesToRead);
                            }
                            catch (Exception ex)
                            {
                                Log($"Lỗi đọc dữ liệu từ network stream: {ex.Message}");
                                throw;
                            }

                            if (bytesRead == 0)
                            {
                                Log($"Kết nối đã đóng trong quá trình nhận file {relPath}");
                                throw new IOException("Kết nối đã đóng đột ngột");
                            }

                            fs.Write(buffer, 0, bytesRead);
                            fileBytesReceived += bytesRead;
                            totalBytesReceived += bytesRead;

                            // Log tiến độ định kỳ
                            TimeSpan elapsed = DateTime.Now - lastLogTime;
                            if (elapsed.TotalSeconds >= 5)
                            {
                                double overallProgress = (i * 1.0 / fileCount) + (fileBytesReceived * 1.0 / fileSize / fileCount);
                                Log($"Tiến độ upload: {overallProgress:P2}, Đã nhận: {totalBytesReceived} bytes");
                                lastLogTime = DateTime.Now;
                            }
                        }

                        // Đảm bảo dữ liệu được ghi hết vào đĩa
                        fs.Flush();
                    }

                    Log($"Đã nhận file {i + 1}/{fileCount}: {relPath} ({fileBytesReceived}/{fileSize} bytes)");
                    successCount++;
                }
                catch (Exception ex)
                {
                    Log($"Lỗi khi nhận file {relPath}: {ex.Message}");

                    // Tiếp tục với file tiếp theo thay vì dừng lại
                    continue;
                }

                // Tạm dừng định kỳ để tránh quá tải
                if (i % 10 == 9)
                {
                    Thread.Sleep(100);
                }
            }

            TimeSpan totalTime = DateTime.Now - startTime;
            double avgSpeed = totalBytesReceived / Math.Max(1, totalTime.TotalSeconds) / 1024;

            try
            {
                writer.WriteLine($"Đã nhận thư mục thành công! ({successCount}/{fileCount} files, {avgSpeed:F1} KB/s)");
                writer.Flush();
                Log($"Đã nhận thư mục {relativePath}: {successCount}/{fileCount} files, {totalBytesReceived} bytes, {avgSpeed:F1} KB/s");
            }
            catch (Exception ex)
            {
                Log($"Lỗi khi gửi xác nhận hoàn thành: {ex.Message}");
            }
        }

        private void btnStopServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isRunning)
                {
                    _isRunning = false;
                    _cancellationTokenSource?.Cancel();

                    try
                    {
                        _listener?.Stop();
                    }
                    catch (ObjectDisposedException)
                    {
                        Log("Listener đã được dispose trước đó khi cố gắng dừng.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Lỗi khi dừng listener: {ex.Message}");
                    }
                    
                    Log("Server đã dừng.");
                    btnStartServer.IsEnabled = true;
                    btnStopServer.IsEnabled = false;
                }
                else
                {
                    Log("Server chưa khởi động.");
                }
            }
            catch (Exception ex)
            {
                Log($"Lỗi dừng server: {ex.Message}");
            }
        }

        private void Log(string msg)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Log(msg));
                return;
            }
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " - " + msg + Environment.NewLine);
            txtLog.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            btnStopServer_Click(this, null);
            base.OnClosed(e);
        }
    }
}