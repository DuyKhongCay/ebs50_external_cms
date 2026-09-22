# Sao lưu / Khôi phục database backend

Mở `http://localhost:6789/Database` **trên máy chạy backend** (thay port nếu đã cấu hình khác).
Chức năng chỉ nhận truy cập loopback trực tiếp, với hostname `localhost` hoặc địa chỉ loopback.
Truy cập từ LAN, hostname khác hoặc request có `Forwarded`/`X-Forwarded-*` bị từ chối bằng HTTP 403.
Không publish chức năng này qua reverse proxy. V1 chưa có đăng nhập quản trị từ xa.

## Sử dụng

1. Chọn **Tạo backup**, chờ hoàn tất rồi **Tải backup**. Export không dừng server,
   nhưng SQLite có thể chặn ghi trong thời gian tạo snapshot.
2. Để khôi phục, chọn ZIP backup và **Upload và kiểm tra**. Xem thời điểm backup,
   phiên bản, số bản ghi và số job chưa hoàn tất. Upload có hiệu lực trong một giờ.
3. Xác nhận thay toàn bộ dữ liệu rồi chọn **Khôi phục database**. File upload chỉ dùng để
   xác nhận restore một lần; nếu thất bại, upload và kiểm tra lại trước khi thử lại.
4. Trong giai đoạn thay database, các request dữ liệu trả HTTP 503 với `Retry-After: 5`.
   Trang bảo trì, trạng thái tác vụ và `/health/live` vẫn hoạt động.
5. Sau restore, cả dispatcher và reconciliation đều tạm dừng, kể cả sau khi restart.
   Kiểm tra cấu hình kết nối EBS trên Bảng điều khiển; các giá trị `SystemSettings` từ backup
   có thể thay đổi địa chỉ trạm hoặc mật khẩu đang dùng.
6. Chọn **Đồng bộ lại theo dữ liệu hiện tại** khi sẵn sàng gửi đến thiết bị.
   Các job `Pending`, `Dispatching`, `Uploaded` cũ chuyển thành `Superseded`.
   Server tăng revision và tạo một job mới cho mỗi tag trong một transaction, xóa các mốc xác nhận cũ,
   sau đó mở lại hai worker. Nhấn lặp khi đã tiếp tục bị từ chối.

Đóng tab sau khi tác vụ được nhận không hủy restore. Trang lưu ID tác vụ gần nhất trong trình duyệt
để có thể theo dõi lại khi mở trang. Trạng thái tag sau restore là dữ liệu lịch sử, chưa xác minh với EBS.
Khôi phục backend không khôi phục database hay hình ảnh thực tế trên trạm/tag.

## Phạm vi và định dạng

ZIP chứa đúng hai file: `manifest.json` và `database.db`. Bao gồm toàn bộ:
`MachineStates`, `ModelItems`, `EslTags`, `DispatchJobs`, `SystemSettings`.
Không bao gồm `Assets`, `appsettings.json`, file SFTP hoặc database trên trạm EBS.
Backup có thể chứa mật khẩu `Ebs50Password`; lưu file tại nơi chỉ người vận hành được truy cập.

Manifest chứa phiên bản format/schema (V1), phiên bản assembly, thời điểm UTC, SHA-256,
số bản ghi từng bảng và số job chưa hoàn tất. Các số liệu lấy từ cùng snapshot đã đóng kết nối.
Checksum phát hiện thay đổi file, không xác thực người tạo backup.

Giới hạn V1: ZIP tối đa 100 MiB; database giải nén tối đa 512 MiB; manifest tối đa 64 KiB.
ZIP có entry thừa, trùng hoặc đường dẫn khác hai tên trên bị từ chối.
Import kiểm tra checksum, metadata, SQLite integrity, khóa ngoại, dữ liệu tag/revision và schema.
Schema phải trùng định nghĩa `sqlite_master` của database đang chạy, kể cả SQL tạo bảng/index.
V1 không tự migrate backup cũ, không nhận file `.db` riêng lẻ và không merge từng bảng.

## Dữ liệu bảo trì và phục hồi

Database được lấy từ `ConnectionStrings:DefaultConnection`; đường dẫn tương đối tính từ
`AppContext.BaseDirectory`, giữ các tùy chọn khác trong connection string.
Thư mục bảo trì là `<database-path>.maintenance`, nằm cạnh database và phải ngoài thư mục public.
Tài khoản Windows Service cần quyền đọc/ghi/thay file trong thư mục database và bảo trì.
Chỉ chạy một instance backend cho database này; `process.lock` ngăn instance thứ hai cùng sử dụng cơ chế bảo trì.
Không mở database bằng công cụ khác trong lúc restore.

```text
etag_database.db
etag_database.db.maintenance/
  process.lock
  restore.json                  # chỉ tồn tại khi restore chưa commit/rollback xong
  sync-paused.json               # tạm dừng đồng bộ sau restore
  restored.json                  # giữ database rỗng hợp lệ, không seed lại dữ liệu mẫu
  <operation-id>/
    operation.json
    backup.zip                  # export
    upload.zip, database.db     # file tạm kiểm tra import
    before.db                   # snapshot trước restore, nếu tác vụ đã tạo
```

File export/tác vụ thường hết hạn sau 24 giờ, upload sau một giờ. Server dọn các file này
mỗi phút và khi khởi động; file đang tải trên Windows được giữ đến khi tải xong.
Thư mục có `before.db` **không tự xóa**. Người vận hành cần quản lý dung lượng các bản phục hồi.
Không xóa `restore.json`, `sync-paused.json`, `restored.json` để bỏ qua một lỗi.

Trước khi thay database, server:

1. Chặn truy cập database mới, chờ scope của request và worker đang chạy được dispose.
2. Tạo và kiểm tra `before.db`, flush xuống đĩa; chuẩn bị database thay thế trên cùng volume.
3. Checkpoint WAL, đóng pool và kết nối, ghi journal phục hồi bền vững.
4. Ghi trạng thái tạm dừng, thay file và kiểm tra lại bằng kết nối mới.
5. Xóa journal sau thành công; giữ bản trước restore và trạng thái tạm dừng đồng bộ.

Nếu lỗi sau khi ghi journal, server rollback từ `before.db`. Nếu rollback cũng lỗi,
database tiếp tục bị chặn và log có đường dẫn bản phục hồi. Giải phóng khóa file/khắc phục dung lượng,
sau đó restart service: recovery chạy **trước DbInitializer và hai worker**.
Journal còn tồn tại luôn dẫn tới rollback, kể cả database mới đã được thay nhưng chưa commit journal.
Nếu startup không phục hồi được, ứng dụng dừng khởi động để tránh seed hoặc chạy trên dữ liệu chưa xác định.

Nếu cần phục hồi thủ công, trước hết dừng service và giữ nguyên bản sao toàn bộ database,
sidecar cùng thư mục bảo trì. `before.db` là snapshot SQLite độc lập, nhưng không phải ZIP upload.
Không copy một file database đang mở để thay cho quy trình backup này.

Thời gian chờ request/worker mặc định 30 giây, điều chỉnh bằng:

```json
"DatabaseMaintenance": {
  "DrainTimeoutSeconds": 30
}
```

Khoảng cho phép 1–300 giây. Hết thời gian chờ thì không thay dữ liệu.
Đây không phải timeout tuyệt đối cho mọi lời gọi native SQLite. SQLite backup và các lệnh ADO.NET
vẫn chạy đồng bộ; chúng được xử lý tuần tự trong background worker, không có fire-and-forget.
Từ khi ghi journal, hoàn tất/rollback không phụ thuộc token của HTTP request.
Journal hỗ trợ khởi động lại sau khi tiến trình bị dừng; độ bền trước mất điện vẫn phụ thuộc filesystem/phần cứng.

## API

Các POST yêu cầu header `X-CSRF-TOKEN` lấy từ hidden field `__RequestVerificationToken`
trên `/Database`, cùng cookie antiforgery của lần tải trang đó. Không vô hiệu CSRF để gọi qua Swagger.

| Endpoint | Kết quả |
| --- | --- |
| `GET /api/database/status` | Trạng thái bảo trì và tạm dừng đồng bộ |
| `POST /api/database/exports` | `202`, ID tác vụ và `Location` để polling |
| `GET /api/database/exports/{id}/download` | ZIP của export đã thành công |
| `POST /api/database/imports/validate` | Multipart field `file`; `202`, ID kiểm tra cũng là import ID |
| `POST /api/database/imports/{id}/restore` | `202`, ID tác vụ restore riêng |
| `GET /api/database/operations/{id}` | Giai đoạn, kết quả, preview, đường dẫn phục hồi nếu có |
| `POST /api/database/resume-sync` | `202`, tạo job mới và mở đồng bộ |

`202` chỉ xác nhận nhận tác vụ; đọc `status` đến `Succeeded` hoặc `Failed`.
`400`: yêu cầu/CSRF/import ID không hợp lệ; `403`: không phải truy cập local;
`409`: đang có tác vụ khác; `413`: upload quá lớn; `404`: không tồn tại hoặc đã hết hạn.
Trong bảo trì, API dữ liệu thông thường trả `503`.

## Kiểm thử

```powershell
dotnet test .\tests\ebs50_backend.Tests\ebs50_backend.Tests.csproj --filter FullyQualifiedName~DatabaseMaintenance
```

Các test dùng SQLite file thật trong thư mục tạm; kiểm tra export khi ghi đồng thời,
restore/resume cả 5 bảng, file sai, khóa Windows, timeout drain, journal trước/sau thay file,
database bị mất/hỏng, chống seed lại database rỗng, và HTTP/Razor/CSRF/Swagger trên loopback.
Không kết nối hoặc gửi dữ liệu đến EBS-50 thật. Thử nghiệm mất điện thực tế, filesystem đầy hoàn toàn
và thao tác trình duyệt trên Windows Service cài đặt thực tế vẫn cần kiểm tra tại môi trường vận hành.
