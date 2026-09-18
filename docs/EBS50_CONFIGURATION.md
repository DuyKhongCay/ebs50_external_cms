# Cấu hình trạm EBS50 và tắt Layering

Đối chiếu: 18/09/2026. Áp dụng cho luồng backend gửi PNG/XML External CMS; controller đã quan sát: **1.0.80.9575**, thư mục cấu hình ứng dụng **3.0.11.9577**. Firmware khác cần đối chiếu lại schema và giao diện.

[README](../README.md) · [Vận hành Windows](OPERATIONS_GUIDE.md) · [MES API](MES_API_GUIDE.md) · [Kết quả kiểm chứng](VERIFICATION.md)

## 1. Phân biệt hai hệ thống

| Thành phần | Backend Windows | Trạm EBS50 Linux |
| --- | --- | --- |
| Dịch vụ | Ebs50TagService | application.service |
| Database | etag_database.db cạnh executable, trừ khi cấu hình đường dẫn khác | /home/root/ebs_50_run/Output/esl.sqlite3 |
| Cấu hình | appsettings.json và bảng SystemSettings | user.config của ứng dụng Opticon |
| Dữ liệu chính | Máy, model, trạng thái, EslTags, DispatchJobs | links, links_staging, labelstatus, images, basestationstatus |
| File nhận | Local_Ebs50_Input là bản lưu cục bộ | Input nhận PNG/XML; Output/Processed lưu file đã được tiếp nhận |

Thay đổi `SystemSettings` của backend không tắt Layering trên trạm. Thay đổi một bản user.config không đúng phiên bản cũng không tác động tới ứng dụng đang chạy.

## 2. Kết nối và nhận diện cấu hình

PowerShell trên Windows; thay địa chỉ và tài khoản bằng thông tin trạm của bạn. SSH yêu cầu mật khẩu tương tác:

```powershell
$stationHost = '<EBS50_IP>'
$stationUser = '<SSH_USER>'
ssh "$stationUser@$stationHost"
```

Trong phiên SSH, dùng lệnh tương thích BusyBox (`ps`, không dùng `ps -ef`):

```sh
date
uname -a
ps | grep EslCoreWebApplication
systemctl status application --no-pager -l
systemctl cat application
find /home/root/.local/share -name user.config
```

Đối chiếu executable trong service, phiên bản startup log và thư mục phiên bản chứa user.config. Nếu có nhiều kết quả, không lấy file đầu tiên một cách tự động. Mẫu đã quan sát:

```text
/home/root/.local/share/Opticon_Sensors_Europe_BV/EslCoreWebApplication_Url_<HASH>/<VERSION>/user.config
```

Đặt biến dùng xuyên suốt cùng phiên SSH sau khi xác định đúng đường dẫn:

```sh
cfg='/home/root/.local/share/Opticon_Sensors_Europe_BV/EslCoreWebApplication_Url_<HASH>/<VERSION>/user.config'
db='/home/root/ebs_50_run/Output/esl.sqlite3'
test -f "$cfg" && test -f "$db"
grep -A2 'name="Layering"' "$cfg"
sqlite3 -readonly "$db" 'SELECT sqlite_version();'
sqlite3 -readonly "$db" '.schema links'
sqlite3 -readonly "$db" '.schema links_staging'
sqlite3 -readonly "$db" 'SELECT count(*) FROM links; SELECT count(*) FROM links_staging;'
```

Nếu lệnh kiểm tra file thất bại, dừng tại đây. `sqlite3` khi thiếu file có thể tạo database mới nếu không dùng `-readonly`.

## 3. External CMS

Mở HTTPS của trạm, kiểm tra chế độ nhận ảnh External CMS và thư mục Input đang sử dụng. Hướng dẫn cũ của project tham chiếu `/Database/CMS` và các nhãn “Configure external CMS”, “Enable external CMS”; đường dẫn và tên nút này **chưa được kiểm chứng lại qua giao diện của phiên bản đang chạy**. Nếu giao diện khác, đối chiếu hướng dẫn đi kèm phiên bản, không đoán số enum `DbFormat` để sửa trực tiếp.

Backend hiện gửi ảnh trước, XML sau; XML có `ImageFile`, `ID`, `Note`, `Label`. File XML cuối cùng mang tên `<MAC>.xml`. `Ebs50LinkSyncService` chỉ lưu links.csv cục bộ, không tự tải file đó lên trạm trong luồng hiện tại.

File xuất hiện trong `Processed` chưa chứng minh liên kết thành công hoặc ảnh đã lên tag. Đọc thêm log và đối soát ảnh.

## 4. Vì sao Layering tự bật lại?

Lỗi đã gặp:

```text
Error linking: Column 'Layer' does not allow nulls.
```

Trong thư viện controller đã kiểm tra, `ReloadLinks()` gọi `EnableLayering()` khi cấu hình và sự hiện diện của cột `Layer` không khớp. Vì thế sửa user.config thành `False` nhưng giữ cột `Layer` trong `links` khiến ứng dụng lưu lại `True` khi khởi động.

Để cấu hình ổn định, schema liên kết và user.config phải cùng ở chế độ không phân lớp. Không thêm `<Layer>` vào XML để sửa: phiên bản đã quan sát báo `Unknown Node: 'Layer'` với cách này.

Đây là cách xử lý cho cấu hình và luồng ảnh đã khảo sát, không phải yêu cầu mọi hệ thống External CMS phải tắt Layering.

## 5. Quy trình khi cả hai bảng liên kết trống

Điều kiện: đúng database/schema đã khảo sát, `links` và `links_staging` đều 0 dòng; đã chọn thời gian bảo trì. Nếu bảng có dữ liệu, chuyển tới mục 6. Nếu đã không còn cột Layer và cấu hình là False, chỉ kiểm tra sau khởi động, không chạy migration lại.

### 5.1. Ngừng nguồn ghi và sao lưu

Ngừng nguồn MES và backend gửi sang trạm. Với Windows Service:

```powershell
Stop-Service Ebs50TagService
Get-Service Ebs50TagService
```

Nếu chạy console thì dừng đúng phiên backend đó. Giữ nguyên service registration.

Trên SSH, dừng ứng dụng trạm:

```sh
systemctl stop application
systemctl show application -p ActiveState -p SubState -p MainPID
ps | grep EslCoreWebApplication
```

Chỉ tiếp tục khi `MainPID=0` và không còn tiến trình ứng dụng. Trạm đã từng báo stop timeout rồi SIGKILL; khi đó đọc `journalctl -u application -n 20 --no-pager`, xác minh đã dừng và kiểm tra SQLite. Không coi mọi trạng thái `failed` là đủ điều kiện để sửa.

Tạo bản sao sau khi ứng dụng dừng để tránh nó ghi đè cấu hình lúc thoát:

```sh
backup_dir="/home/root/ebs50_layering_backup_$(date +%Y%m%d_%H%M%S)"
mkdir -m 700 "$backup_dir" &&
cp -p "$cfg" "$backup_dir/user.config" &&
sqlite3 -readonly "$db" ".backup '$backup_dir/esl.sqlite3'" &&
sqlite3 -readonly "$backup_dir/esl.sqlite3" 'PRAGMA integrity_check;'
```

Kết quả cuối phải là `ok`; nếu có lỗi thì dừng, không chạy bước tiếp theo. Ghi lại đường dẫn backup và giờ Windows/giờ trạm. Lưu thêm đầu ra `.schema` nếu cần đối chiếu. Sao chép backup ra nơi lưu trữ riêng trước bảo trì dài hạn.

### 5.2. Chuyển schema có bảo vệ

File thực thi chuẩn: [disable-layering-empty.sql](sql/disable-layering-empty.sql). File này dùng `BEGIN IMMEDIATE`, kiểm tra bảng trống, cột/khóa chính, index/trigger tùy biến, view và foreign key. Bất kỳ kiểm tra nào thất bại phải làm dừng kết nối và rollback. SQLite phải hỗ trợ các hàm `pragma_table_info`/`pragma_foreign_key_list`; nếu không hỗ trợ thì dừng để đối chiếu phiên bản.

Từ một cửa sổ PowerShell khác tại thư mục project, tải đúng file SQL lên thư mục backup vừa tạo:

```powershell
$stationBackup = '/home/root/ebs50_layering_backup_<TIMESTAMP>'
scp .\docs\sql\disable-layering-empty.sql "${stationUser}@${stationHost}:$stationBackup/disable-layering-empty.sql"
```

Trong SSH, xác nhận nội dung và chạy với `-bail`:

```sh
sql="$backup_dir/disable-layering-empty.sql"
cat "$sql"
sqlite3 -bail "$db" < "$sql"
```

Nếu exit code khác 0 hoặc có lỗi, **không tiếp tục sửa cấu hình hoặc start ứng dụng**; kiểm tra schema và bản sao. Không bỏ điều kiện bảo vệ để ép chạy. Kiểm tra các trigger ở bảng khác có tham chiếu links trong `.schema` trước chạy nếu trạm đã được tùy biến.

Schema mong đợi:

```text
links:         ID VARCHAR(40) NOT NULL, Variant VARCHAR(16) NOT NULL,
               MAC VARCHAR(16) NOT NULL PRIMARY KEY
links_staging: ID VARCHAR(40), Variant VARCHAR(16),
               MAC VARCHAR(16) NOT NULL PRIMARY KEY, DELETE VARCHAR(1)
```

Kiểm tra:

```sh
sqlite3 -readonly "$db" 'PRAGMA table_info(links); PRAGMA table_info(links_staging); PRAGMA integrity_check;'
```

Không còn `Layer`, khóa chính chỉ có MAC, cột `DELETE` vẫn tồn tại và integrity trả `ok`.

### 5.3. Đặt False và khởi động

Kiểm tra file có đúng một setting Layering theo định dạng nhiều dòng đã khảo sát. Lệnh sed dưới đây chỉ thay `True` trong setting đó; cấu trúc XML khác cần sửa bằng trình soạn thảo XML phù hợp.

```sh
test "$(grep -c 'name="Layering"' "$cfg")" -eq 1 &&
sed -i '/<setting name="Layering"/,/<\/setting>/s/<value>True<\/value>/<value>False<\/value>/' "$cfg"
grep -A2 'name="Layering"' "$cfg"
diff -u "$backup_dir/user.config" "$cfg"
```

Điều kiện đếm phải đạt đúng 1; giá trị sau sửa là False, diff chỉ có thay đổi dự kiến. Nếu không đúng, khôi phục theo mục 7 trước khi start.

```sh
systemctl start application
systemctl status application --no-pager -l
```

Chờ startup log `ESL Controller started` của tiến trình mới rồi kiểm tra:

```sh
current_pid=$(systemctl show application -p MainPID --value)
journalctl -u application _PID="$current_pid" -n 40 --no-pager -o cat
grep -A2 'name="Layering"' "$cfg"
sqlite3 -readonly "$db" 'PRAGMA table_info(links); PRAGMA table_info(links_staging); PRAGMA integrity_check; SELECT MAC,STATUS,ESLS FROM basestationstatus;'
```

Tiêu chí: service running, False được giữ, không còn Layer trong hai bảng, database `ok`, không có lỗi Layer mới. `Connected` là kết nối trạm; kiểm tra cả số tag và dữ liệu liên lạc.

Sau khi đạt kiểm tra cấu hình, bật lại backend. Thử đúng một tag đã chọn theo [hướng dẫn MES](MES_API_GUIDE.md), ghi giờ, MAC, trạng thái yêu cầu, job và ảnh thực tế. Không dùng send-all để thử lần đầu.

## 6. Nếu bảng có dữ liệu

Quy trình trên phải từ chối. Không xóa dữ liệu để vượt điều kiện kiểm tra. Thu thập trên database chỉ đọc:

```sql
SELECT count(*) FROM links;
SELECT count(*) FROM links_staging;
SELECT MAC, count(*) FROM links GROUP BY MAC HAVING count(*) > 1;
SELECT MAC, ID, Variant, Layer FROM links ORDER BY MAC, Layer;
```

Nếu schema không có Layer, bỏ truy vấn cuối và đối chiếu `.schema` trước. Một MAC có nhiều lớp phải có quyết định rõ liên kết nào được giữ/gộp. Bản ghi staging phải được xử lý hoặc bảo toàn theo nghiệp vụ. Chức năng DisableLayering trong thư viện đã khảo sát có lựa chọn một liên kết theo MAC; không xem đó là quy tắc chọn lớp đúng cho dữ liệu của bạn.

Thử chức năng tắt Layering qua ứng dụng hoặc migration có dữ liệu trên bản sao/môi trường thử trước. Đối chiếu MAC, ID, Variant và dữ liệu staging trước/sau, kiểm tra ảnh và khôi phục. **Chưa xác minh quy trình migration cho bảng có dữ liệu; tài liệu này không cung cấp lệnh xóa/gộp tự động cho nhánh đó.**

## 7. Khôi phục database và cấu hình cùng mốc

Ngừng backend và application.service; xác nhận MainPID=0 như mục 5.1. Khôi phục sẽ mất thay đổi phát sinh sau backup. Chọn đúng `$backup_dir`, `$db`, `$cfg` trong phiên SSH; không dùng timestamp của ca cũ làm mặc định.

```sh
test -f "$backup_dir/esl.sqlite3" && test -f "$backup_dir/user.config"
sqlite3 -readonly "$backup_dir/esl.sqlite3" 'PRAGMA integrity_check;'
```

Chỉ tiếp tục khi cả hai file tồn tại và kết quả `ok`. Lưu lại database lỗi nếu cần điều tra; dùng SQLite restore thay cho chép đè tùy tiện file đang có WAL:

```sh
sqlite3 "$db" ".backup '$backup_dir/esl.before-restore.sqlite3'" &&
sqlite3 -bail "$db" ".restore '$backup_dir/esl.sqlite3'" &&
cp -p "$backup_dir/user.config" "$cfg" &&
sqlite3 -readonly "$db" 'PRAGMA integrity_check;'
```

Nếu database hỏng không thể backup, giữ nguyên các file để điều tra và dùng phương án phục hồi riêng; không tiếp tục một chuỗi lệnh đã lỗi. Xác minh quyền file, `.schema links`, Layering và tính nhất quán trước start. Khôi phục về cấu hình cũ có thể làm lỗi cũ xuất hiện lại; đó là hoàn tác thay đổi, không phải xác nhận đã chữa lỗi.

## 8. Bằng chứng và giới hạn

Ca đã thực hiện có backup trên trạm tại `/home/root/ebs50_layering_backup_20260417_224423`. Timestamp này theo đồng hồ trạm bị lệch; không dùng để suy ra ngày thực tế. Việc giữ False qua startup đã xác nhận trong phiên sửa trước. Đợt tài liệu hiện tại kiểm thử SQL trên database thử theo schema đã thu thập, không gửi lại ảnh hay restart trạm.

Đọc [VERIFICATION.md](VERIFICATION.md) để phân biệt kiểm tra ngoại tuyến, bằng chứng lịch sử và kiểm tra phần cứng còn thiếu.
