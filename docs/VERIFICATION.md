# Kết quả kiểm chứng tài liệu và metadata API

Ngày: 18/09/2026. [README](../README.md) · [Kế hoạch](DOCUMENTATION_PLAN.md).

## 1. Phạm vi thực hiện

Đã tạo README, hướng dẫn cấu hình EBS50, hướng dẫn MES và file SQL bảo vệ khi tắt Layering. Đã mở rộng OPERATIONS_GUIDE và nối XML documentation vào Swagger. Code API chỉ thay metadata và thay anonymous response của Dispatch bằng record có cùng tên trường/giá trị; không đổi route, mã HTTP hoặc quy trình gửi/đối soát.

Nguồn đối chiếu:

- [Program.cs](../Program.cs), [project](../ebs50_backend.csproj): .NET 8, Controllers, Swashbuckle, đường dẫn dữ liệu, service/cổng.
- [Controllers](../Controllers), [DTOs](../DTOs): hợp đồng và lỗi thực tế.
- [SFTP](../Services/Dispatching/Ebs50SftpService.cs), [status service](../Services/Dispatching/Ebs50StatusService.cs): cấu hình, thứ tự ưu tiên và cách đọc trạm.
- [worker gửi](../Services/Dispatching/EslDispatcherBackgroundWorker.cs), [worker đối soát](../Services/Dispatching/Ebs50StatusReconciliationWorker.cs): queue, retry và xác nhận.
- [initializer](../Services/DbInitializer.cs), [tag service](../Services/Core/TagService.cs): seed, schema và tạo job.
- [scripts](../scripts), [installer](../installer/ebs50_setup.iss): triển khai và bảo toàn dữ liệu.
- Log/schema và mã controller trạm đã kiểm tra trong phiên sửa Layering trước; chưa mở lại giao diện CMS hoặc thay đổi trạm trong đợt này.

## 2. Kiểm chứng đã chạy

### Build và publish

Đã build và publish self-contained win-x64 với .NET SDK 8.0.425. Lệnh publish từ thư mục repository:

```powershell
dotnet publish ebs50_backend/ebs50_backend.csproj -c Release -r win-x64 --self-contained true --no-restore -o .diagnostics/documentation/publish
```

Publish thành công; có XML documentation, Assets và wwwroot. Warning XML do dấu `&` trong comment cũ được sửa trước lần publish thành công. Không chạy installer, tạo service hoặc sửa firewall trên máy làm việc.

### SQL trên database thử

Dùng Microsoft.Data.Sqlite 8.0.13, tạo fixture theo schema links/links_staging đã thu thập từ trạm và thêm một dòng labelstatus để kiểm tra dữ liệu không liên quan. Đây là database thử dựng lại schema, không phải bản sao toàn bộ database sản xuất.

| Trường hợp | Kết quả |
| --- | --- |
| Cả hai bảng trống, schema đúng | Migration thành công; MAC là khóa chính; Layer bị bỏ; DELETE được giữ |
| links có dữ liệu | Từ chối; đóng connection rollback; schema và dữ liệu liên quan được bảo toàn |
| links_staging có dữ liệu | Từ chối; schema rollback |
| Một MAC có hai lớp | Từ chối; giữ cả hai dòng |
| Có cột tùy biến | Từ chối; schema rollback |
| Có index tùy biến | Từ chối; schema rollback |
| Có view phụ thuộc | Từ chối; schema rollback |
| Có foreign key phụ thuộc | Từ chối; schema rollback |
| Backup rồi migration rồi restore bằng SQLite backup API | Khôi phục schema cũ và dữ liệu labelstatus |

Mọi fixture đều trả integrity `ok`, dòng labelstatus không bị thay đổi. Ngữ nghĩa đóng connection sau lỗi mô phỏng `sqlite3 -bail`; chưa chạy lại file SQL mới trên SQLite binary/firmware thật. Quy trình cấu hình và schema tương ứng đã được thực hiện trên trạm trong phiên trước.

### API trên backend tách biệt

Chạy executable publish với ContentRoot và database mới trong thư mục temp, cổng riêng. Tắt EventLog provider cho tiến trình thử vì sandbox không có quyền ghi source; console log vẫn được thu thập. Cấu hình transport trước khi tạo job thành **127.0.0.1:1** để các phép thử chỉ thất bại kết nối cục bộ, không chạm EBS50.

Các kiểm tra đã đạt:

- Dashboard, Swagger UI và OpenAPI JSON trả thành công.
- Đủ 25 operation, mỗi operation có summary từ XML và operationId duy nhất.
- 202 Dispatch dùng schema đặt tên; 400 của luồng MES mô tả cả validation lẫn lỗi nghiệp vụ; preview dùng schema binary image/png.
- Danh mục mới có 5 trạng thái; GET cấu hình và POST send-tag giữ tên trường JSON của hợp đồng cũ.
- Lưu cấu hình trả 200; Test Connection lỗi trả HTTP 200 nhưng `success=false`.
- Máy không tồn tại trả 404; link tag thử, đổi trạng thái và GET lại tag trả dữ liệu phù hợp.
- Mã -1 trả 400 application/problem+json; mã 999 chưa có trong danh mục trả 400 application/json.
- JSON sai trả 400; Content-Type sai trả 415.
- send-tag/send-all trả 202; queue status trả 200.
- Simulate trả image/png với signature PNG hợp lệ.
- Gửi `{}` hiện trả 200 và áp dụng stateCode=0; ghi thành hạn chế, không sửa nghiệp vụ trong đợt này.

Harness cục bộ ghi nhận **78 kiểm tra đạt** trong lần chạy đầy đủ; log, OpenAPI và response mẫu nằm tại `.diagnostics/documentation/evidence` ở workspace kiểm chứng. Đây là bằng chứng kiểm tra theo kịch bản, không phải cam kết toàn bộ nhánh API đã được kiểm thử hoặc một bộ unit test mới của sản phẩm.

## 3. Những gì chưa xác minh trong đợt này

| Hạng mục | Trạng thái |
| --- | --- |
| Gửi ảnh và quan sát tag thật sau sửa Layering | Chưa thực hiện |
| Migration Layering khi links/staging có dữ liệu | Chưa cung cấp quy trình thực thi; guard từ chối |
| Chức năng DisableLayering qua giao diện của từng firmware | Chưa kiểm tra UI |
| URL/nút External CMS trên firmware hiện tại | Ghi rõ tham chiếu lịch sử, chưa xác nhận lại |
| Cài/gỡ/nâng cấp Windows Service bằng installer | Đối chiếu script, chưa thực thi thao tác quản trị |
| Toàn bộ quy trình restore trên thiết bị đang vận hành | Backup API đã thử trên fixture; chưa diễn tập lại thiết bị thật |
| Độ chính xác của pin, enum RF và revision xác nhận | Không suy ra từ thành công upload hoặc Connected |

Bằng chứng lịch sử: trong phiên trước, trạm giữ `Layering=False` sau startup, service running và SQLite integrity ok; chỉ hai bảng liên kết trống được chuyển đổi. Trạm báo Connected nhưng chưa có bằng chứng màn hình tag nhận ảnh mới. Đồng hồ trạm bị lệch nên timestamp backup chỉ là nhãn theo giờ trạm.

## 4. Công việc kỹ thuật cần xử lý riêng

Những điểm này được phát hiện qua code/kiểm thử và đã phản ánh vào hướng dẫn, không được sửa ngoài phạm vi tài liệu:

| Ưu tiên | Vấn đề | Tiêu chí cho thay đổi tiếp theo |
| --- | --- | --- |
| Cao | Confirmed chưa gắn ảnh trạm với đúng revision/job mới nhất | Trường hợp ACK ảnh cũ không được xác nhận revision mới; kiểm thử với phần cứng |
| Cao | API cấu hình trả password, API chưa có xác thực | Có mô hình truy cập phù hợp và không trả bí mật trong response đọc cấu hình |
| Cao | `{}` mặc định stateCode=0 dù request có Required trên int | Payload thiếu trường trả 400; đánh giá tương thích client trước đổi |
| Vừa | AutoDispatch=false vẫn tạo job được polling | Làm rõ/điều chỉnh hợp đồng và kiểm tra không gửi ngoài ý định |
| Vừa | POST đổi state có thể trả tên/màu navigation property cũ | Response nhất quán với stateCode sau thay đổi |
| Vừa | SFTP, status service, GET config có fallback/password rỗng khác nhau | Một chính sách cấu hình có kiểm thử, tránh đọc/ghi nhầm trạm |
| Vừa | send-tag newStateCode thiếu kiểm tra tồn tại như MES API | Mã không hợp lệ có lỗi nghiệp vụ rõ, không chờ lỗi database |
| Vừa | Test Connection chưa chứng minh quyền ghi, Input thiếu vẫn success=true | Hợp đồng phân biệt kết nối, đường dẫn và kiểm tra ghi |
| Vừa | File XML được upload trực tiếp vào tên cuối; comment atomic không đủ chứng minh an toàn đọc đồng thời | Xác minh giao thức file với firmware trước thay đổi transport |
| Vừa | Cài/nâng cấp có khoảng chờ cố định, script build installer có thể exit 0 khi thiếu compiler | Kiểm tra stop thật, bảo toàn dữ liệu và xác nhận artifact được tạo |

## 5. Duy trì và nghiệm thu phần cứng

Khi có tag thử, ghi MAC, nội dung mong đợi, request, JobId/revision, log upload, image IDs/file trên trạm và ảnh màn hình sau cập nhật. Chỉ đánh dấu nghiệm thu phần cứng khi các bằng chứng khớp cùng lần gửi. Không cần chạy lại thay đổi schema trên trạm đã cấu hình đúng để hoàn thiện tài liệu.

Đã kiểm tra 6 file Markdown: 53 liên kết nội bộ tồn tại, 7 ví dụ JSON parse được, 13 block PowerShell không có lỗi cú pháp; code fence cân bằng và không có ký tự thay thế do lỗi UTF-8. Kiểm tra cú pháp không đồng nghĩa đã chạy các lệnh quản trị trong ví dụ.

Chỉnh sửa route/DTO, cấu hình, firmware hoặc script triển khai phải cập nhật tài liệu tương ứng trong cùng thay đổi.
