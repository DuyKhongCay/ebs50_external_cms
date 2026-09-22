# Web server trên hai mạng

Kestrel lắng nghe mọi interface qua `ServiceSettings:Port` (mặc định 6789).
Hai mạng truy cập cùng ứng dụng và database. Không cần bridge, ICS, NAT hoặc IP forwarding.

## Cấu hình máy chủ

Ví dụ cần thay bằng địa chỉ được cấp thực tế:

| Vai trò | IP server | Subnet thiết bị | Gateway |
| --- | --- | --- | --- |
| Văn phòng | 192.168.1.10/24 | 192.168.1.0/24 | Gateway văn phòng |
| EBS | 192.168.11.10/24 | 192.168.11.0/24 | Để trống nếu chỉ liên lạc trong subnet |

Ghi lại IP, DNS, route và firewall trước khi chỉnh. Hai subnet không chồng lấn,
địa chỉ không trùng, dùng IP tĩnh hoặc DHCP reservation. Script của ứng dụng không sửa IP.
Mỗi client truy cập IP server thuộc mạng của mình.

## Firewall và cài đặt

Chạy PowerShell với quyền Administrator. Ví dụ từ thư mục bản publish:

```powershell
Get-NetAdapter
Get-NetIPConfiguration
Get-NetRoute -AddressFamily IPv4

.\scripts\Configure-NetworkFirewall.ps1 -ConfigPath .\appsettings.json `
  -InterfaceAliases 'Wi-Fi','Ethernet' `
  -RemoteSubnets '192.168.1.0/24','192.168.11.0/24' `
  -BackupPath .\firewall-before.clixml -WhatIf
```

Sau khi kiểm tra phạm vi, chạy lại bỏ `-WhatIf`. Thứ tự interface và subnet phải tương ứng.
Rule áp dụng mọi profile nhưng chỉ trên interface và subnet được chỉ định. Kiểm tra cả rule
của phần mềm khác/GPO có mở rộng quyền truy cập hay chặn truy cập hay không.
Script thay thế rule cũ `EBS-50 E-Tag Service (Port ...)` và hai rule `Ebs50-Web-Network-*`.
Mỗi lần áp dụng dùng BackupPath mới; không ghi đè bản hoàn tác. Nếu áp dụng lỗi, script cố gắng khôi phục rule trước đó.

```powershell
.\scripts\Configure-NetworkFirewall.ps1 -Restore -BackupPath .\firewall-before.clixml -WhatIf
.\scripts\Configure-NetworkFirewall.ps1 -Restore -BackupPath .\firewall-before.clixml
```

Chỉ dùng backup do script tạo và quản trị viên kiểm soát. Hoàn tác khôi phục phạm vi rule cũ,
kể cả rule rộng nếu trước đó có. Không sửa IP, DNS, route hay service.

Cài Windows Service bằng script hiện yêu cầu đủ hai interface/subnet:

```powershell
.\scripts\Install-Service.ps1 -BinaryPath .\ebs50_backend.exe -Port 6789 `
  -InterfaceAliases 'Wi-Fi','Ethernet' `
  -RemoteSubnets '192.168.1.0/24','192.168.11.0/24'
```

`-Port` phải khớp `ServiceSettings:Port` trong appsettings cạnh exe. Nếu dùng biến môi trường
ghi đè cổng, đồng bộ giá trị trong file trước khi chạy script. Trình cài Inno Setup không còn
tạo rule mở rộng; sau cài đặt chạy Configure-NetworkFirewall như trên. Khi nâng cấp, script
loại bỏ rule rộng cũ của ứng dụng. Nếu cài service thất bại sau khi firewall đã áp dụng,
dùng backup được tạo trong thư mục exe để hoàn tác firewall.

## Giao diện và API

Mở `/Network`. Trang hiển thị interface ID, trạng thái, IPv4/prefix, gateway và URL.
Gán `NetworkSettings:OfficeInterfaceId` và `NetworkSettings:EbsInterfaceId` trong appsettings
bằng ID hiển thị (khởi động lại service sau khi đổi cấu hình). Đây chỉ là nhãn vai trò,
không ràng buộc socket hay sửa route. Card chưa gán vẫn hiển thị. Loopback được bỏ qua;
card mất kết nối không có URL và địa chỉ tự cấp 169.254.x.x không được đề xuất truy cập.
MVP hiển thị IPv4; firewall ví dụ cũng chỉ cho IPv4.

- `GET /api/network/interfaces`: thông tin interface và URL ứng viên.
- `POST /api/network/diagnostics`: HTTP nội bộ và TCP tới EBS theo cấu hình hiệu lực, không nhận host tùy ý.
- `GET /health/live`: ứng dụng web còn phục vụ, độc lập trạng thái EBS.

Chỉ một lượt chẩn đoán đồng thời trên mỗi tiến trình (lượt khác nhận 429), tối đa bốn probe
song song; TCP timeout 3 giây, HTTP 5 giây, deadline tổng 10 giây. Caller cancellation truyền
xuống I/O. Kết quả có trạng thái, mã lỗi, thời gian và endpoint nguồn TCP. Một probe lỗi không
làm mất kết quả các probe còn lại. HTTP thành công trên server không chứng minh truy cập từ xa.
TCP thành công không chứng minh xác thực SFTP; dùng kiểm tra trạm trên dashboard cho SFTP.
Ứng dụng hiện chưa yêu cầu token: giới hạn subnet đồng thời giới hạn đối tượng truy cập API hiện hữu.

## Nghiệm thu trên mạng thật

1. Từ máy văn phòng, mở `http://192.168.1.10:6789`, `/Network`, `/health/live` và Swagger.
2. Từ máy mạng EBS, kiểm tra tương tự qua `192.168.11.10`; xác nhận cùng dữ liệu.
3. Gửi một tag thử được phép và kiểm tra kết quả trên EBS.
4. Rút từng kết nối: web ở mạng còn lại vẫn hoạt động; làm mới trang để xem trạng thái card.
5. Kiểm tra EBS tắt/cổng đóng, hủy lượt chẩn đoán, hai lượt đồng thời và restart service.
6. Chạy lại script với backup mới: chỉ có hai rule theo tên cố định; kiểm tra hoàn tác trên máy thử.

Hoàn tác ứng dụng bằng bản publish trước. Tính năng không thay schema database.
