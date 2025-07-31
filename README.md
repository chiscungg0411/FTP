# Ứng Dụng FTP Client-Server trên WPF

## Tổng Quan

Dự án này triển khai một ứng dụng FTP (File Transfer Protocol - Giao thức Truyền Tệp) client và server cơ bản bằng cách sử dụng WPF (Windows Presentation Foundation) trong C#. Nó cho phép người dùng truyền tệp và thư mục giữa client và server, cung cấp một giao diện người dùng đồ họa để tương tác dễ dàng.

## Tính Năng

### Server
-   **Server Đa Luồng**: Xử lý nhiều kết nối client đồng thời.
-   **Các Lệnh FTP Cơ Bản**: Hỗ trợ các lệnh FTP thiết yếu như `LIST`, `CD`, `CDUP`, `GET`, `PUT`, `DELETE`, `MKDIR`, và các lệnh truyền thư mục (`GETDIR`, `PUTDIR`).
-   **Ghi Nhật Ký (Logging)**: Ghi lại các hoạt động của server và lệnh của client trong một hộp văn bản thân thiện với người dùng.
-   **Thống Kê**: Cung cấp thống kê thời gian thực về số lượng kết nối đang hoạt động, số lượng tệp đã truyền và dữ liệu đã truyền.
-   **Chỉ Báo Trạng Thái**: Hiển thị trực quan trạng thái của server (đang chạy hoặc đã dừng).
-   **Xóa Nhật Ký**: Khả năng xóa nhật ký server.
-   **Bảo Mật**: Ngăn người dùng xóa thư mục gốc của server.

### Client
-   **Giao Diện Thân Thiện Với Người Dùng**: Giao diện người dùng đồ họa (GUI) dựa trên WPF trực quan để điều hướng và quản lý tệp dễ dàng.
-   **Duyệt Thư Mục**: Cho phép duyệt cả thư mục cục bộ và thư mục trên server.
-   **Truyền Tệp**: Hỗ trợ tải lên và tải xuống các tệp giữa client và server.
-   **Truyền Thư Mục**: Hỗ trợ tải lên và tải xuống toàn bộ thư mục.
-   **Quản Lý Kết Nối**: Dễ dàng kết nối và ngắt kết nối khỏi server.
-   **Ghi Nhật Ký (Logging)**: Ghi lại các hoạt động của client và phản hồi của server trong một hộp văn bản.
-   **Chỉ Báo Trạng Thái**: Phản hồi thời gian thực về trạng thái kết nối và tiến trình truyền.
-   **Chọn Thư Mục Cục Bộ**: Cho phép người dùng chọn thư mục đích tải xuống.

## Công Nghệ Sử Dụng

-   **WPF (.NET Framework)**: Để tạo giao diện người dùng đồ họa.
-   **C#**: Ngôn ngữ lập trình chính.
-   **System.Net.Sockets**: Để triển khai giao tiếp mạng.
-   **System.IO**: Cho các hoạt động tệp và thư mục.
-   **Threading**: Được sử dụng để xử lý các kết nối client đồng thời trên server.

## Thiết Lập

### Yêu Cầu Tiên Quyết
-   [Visual Studio](https://visualstudio.microsoft.com/)
-   .NET Framework 4.7.2 trở lên

### Xây Dựng Dự Án
1.  Sao chép kho lưu trữ:

    ```bash
    git clone https://github.com/chiscungg0411/FTP.git
    ```
2.  Mở giải pháp trong Visual Studio.
3.  Xây dựng giải pháp để khôi phục các gói NuGet và biên dịch mã.

### Chạy Ứng Dụng
1.  Xây dựng cả hai dự án `FTPServerWPF` và `FTPClient`.
2.  Chạy ứng dụng `FTPServerWPF` trước.
3.  Chạy ứng dụng `FTPClient`.
4.  Trong ứng dụng client, nhập địa chỉ IP của server (mặc định là `127.0.0.1`) và nhấp vào "Kết Nối".

## Sử Dụng

### Server

1.  Khởi động server bằng cách nhấp vào nút "Khởi Động Server".
2.  Nhật ký server sẽ hiển thị tất cả các hoạt động, bao gồm kết nối client và thực thi lệnh.
3.  Dừng server bằng cách nhấp vào nút "Dừng Server".

### Client

1.  Nhập địa chỉ IP của server vào hộp văn bản "Server".
2.  Nhấp vào "Kết Nối" để thiết lập kết nối với server.
3.  Duyệt các tệp trên server bằng danh sách tệp server.
4.  Duyệt các tệp cục bộ bằng danh sách tệp cục bộ.
5.  Chọn một tệp hoặc thư mục và nhấp vào "Tải Xuống" để tải xuống từ server.
6.  Chọn một tệp hoặc thư mục và nhấp vào "Tải Lên" để tải lên server.
7.  Sử dụng các nút "Xóa" để xóa tệp hoặc thư mục từ các vị trí tương ứng.
8.  Nút "Làm Mới" tải lại danh sách tệp.
9.  Tất cả các hoạt động được ghi lại trong hộp văn bản "Nhật Ký Hoạt Động".

## Cấu Trúc Mã

-   **FTPServerWPF**:
    -   `MainWindow.xaml`: Định nghĩa bố cục UI cho ứng dụng server.
    -   `MainWindow.cs`: Chứa logic để khởi động, dừng và xử lý các kết nối client.
-   **FTPClient**:
    -   `MainWindow.xaml`: Định nghĩa bố cục UI cho ứng dụng client.
    -   `MainWindow.cs`: Chứa logic để kết nối đến server, duyệt tệp và xử lý truyền tệp.

## Đóng Góp

Rất hoan nghênh các đóng góp! Vui lòng làm theo các bước sau:
1.  Fork kho lưu trữ.
2.  Tạo một nhánh mới cho tính năng hoặc sửa lỗi của bạn.
3.  Commit các thay đổi của bạn.
4.  Đẩy nhánh của bạn lên GitHub.
5.  Gửi một pull request.

## Hình ảnh từ dự án

### Server
<img width="644" height="333" alt="image" src="https://github.com/user-attachments/assets/3c8ef1ed-105d-40ab-98a2-5f1dda05dee1" />

### Client
<img width="544" height="336" alt="image" src="https://github.com/user-attachments/assets/6d50d9f9-34bd-4965-b0c1-c327fa359e8f" />


## Liên Hệ

Nếu có câu hỏi hoặc phản hồi, vui lòng liên hệ:

-   **Email**: Địa chỉ email của bạn (Tùy chọn)
-   **GitHub**: [chiscungg0411](https://github.com/chiscungg0411)
