# Tích hợp MES với EBS50 backend

Đối chiếu: 18/09/2026; .NET 8, API v1. Các ví dụ dùng máy/MAC thử, không phải thiết bị sản xuất. [README](../README.md) · [Vận hành](OPERATIONS_GUIDE.md) · [Kiểm chứng](VERIFICATION.md).

## 1. Hợp đồng chung

Base URL: `http://<BACKEND_IP>:6789`. Swagger: `/swagger`; schema: `/swagger/v1/swagger.json`. Hiện API không yêu cầu token; không có cơ chế idempotency key hoặc API tra riêng một JobId.

Request JSON dùng `Content-Type: application/json`. Response JSON dùng camelCase. Hầu hết controller nghiệp vụ dùng:

```json
{ "success": true, "message": "...", "data": {} }
```

Dispatch controller trả object riêng, không bọc bằng `data`. Lỗi validation tự động của ASP.NET là `ValidationProblemDetails`, còn lỗi nghiệp vụ có thể là `ApiResponse` hoặc chuỗi. Không chỉ dựa vào một trường `success` cho mọi response.

`stateCode` là trạng thái máy, khác `syncStatus` của tag và `status` trong DispatchJobs. Luôn lấy danh mục hiện tại từ `GET /api/States`.

## 2. Luồng tích hợp đề nghị

| Bước | Method và route | Kết quả |
| --- | --- | --- |
| 1 | GET `/api/States` | Danh mục mã trạng thái đang cấu hình |
| 2 | GET `/api/Machines/{machineNo}/tag` | Tag đang gắn với máy, hoặc 404 |
| 3 | POST `/api/Machines/{machineNo}/state` | HTTP 200: backend đã nhận thay đổi và tạo job |
| 4 | GET `/api/Tags/{mac}` hoặc GET theo máy | Snapshot mới của trạng thái backend |
| 5 | Đối soát trạm và màn hình tag khi nghiệm thu | Xác minh nội dung vật lý đúng lần gửi |

Dùng mã máy không chứa dấu `/`, ví dụ `LINE-01`. Không giả định URL-encode dấu `/` luôn được router/server xử lý như một phần route parameter. Model có dấu `/` vẫn có thể nằm trong JSON; tránh đưa model đó vào route xóa nếu chưa kiểm tra đường đi URL.

### Ví dụ PowerShell

Thay `$base`, `$machineNo` bằng backend và máy đã gắn tag:

```powershell
$base = 'http://localhost:6789'
$machineNo = 'LINE-01'
$states = Invoke-RestMethod "$base/api/States"
$tag = Invoke-RestMethod "$base/api/Machines/$machineNo/tag"
$body = @{ stateCode = 3 } | ConvertTo-Json
$accepted = Invoke-RestMethod -Method Post -Uri "$base/api/Machines/$machineNo/state" -ContentType 'application/json' -Body $body
$snapshot = Invoke-RestMethod "$base/api/Tags/$($tag.data.macAddress)"
$snapshot.data
```

`stateCode=3` chỉ dùng khi danh mục thực tế có mã này. Luôn gửi trường stateCode: với kiểu `int` hiện tại, body `{}` bị hiểu thành mã 0; đây là giới hạn đã kiểm thử, không phải cách gửi lệnh hợp lệ được khuyến nghị.

### Ví dụ curl

Trong shell hỗ trợ dấu nháy đơn (Linux/macOS):

```sh
curl -i 'http://localhost:6789/api/Machines/LINE-01/tag'
curl -i -X POST 'http://localhost:6789/api/Machines/LINE-01/state' \
  -H 'Content-Type: application/json' \
  --data '{"stateCode":3}'
```

Với Windows PowerShell, dùng ví dụ Invoke-RestMethod phía trên để tránh khác biệt xử lý dấu nháy của native executable.

### Snapshot tag

Ví dụ cấu trúc JSON sau một lần GET (giá trị và message thay đổi theo dữ liệu):

```json
{
  "success": true,
  "message": "Machine tag found.",
  "data": {
    "macAddress": "D0C00001",
    "machineNo": "LINE-01",
    "modelCode": "MODEL-TEST",
    "stateCode": 3,
    "stateNameVi": "Sự cố máy, chờ sửa",
    "stateNameKo": "*설비 고장, 수리 대기",
    "themeColor": "Red",
    "batteryLevel": 100,
    "syncStatus": "Pending",
    "lastSyncedAt": null
  }
}
```

POST đổi trạng thái có thể trả stateCode mới cùng tên/màu từ navigation property cũ trong cùng request. Khi cần tên/màu mới nhất, GET lại tag. `batteryLevel` là dữ liệu backend; worker đối soát hiện không cập nhật pin từ trạm nên không xem đây là phép đo pin trực tiếp.

## 3. Theo dõi đồng bộ

| Trạng thái | Nơi xuất hiện | Ý nghĩa |
| --- | --- | --- |
| Pending | Tag/job | Chờ xử lý; tag Pending không nhất thiết chứng minh có job đang chờ |
| Dispatching | Tag/job | Worker đang render/gửi hoặc snapshot tag chưa được đồng bộ lại khi job retry |
| Uploaded | Tag/job | Hoàn tất chuyển payload; chưa xác nhận hiển thị |
| Confirmed | Tag/job | Đạt điều kiện đối soát hiện tại của phần mềm |
| Superseded | Job | Job bị thay thế, tag bị xóa hoặc binding version không còn phù hợp |
| Failed | Job | Gửi thất bại sau số lần thử tối đa hiện tại |
| Error | Tag | Worker đánh dấu lỗi gửi cuối cùng |

Worker gửi thử tối đa 3 lần. Công thức retry hiện tại tạo khoảng chờ 10 giây và 20 giây sau lần lỗi thứ nhất/thứ hai; không lấy comment “5s, 10s” trong code làm thông số. Khoảng cách tối thiểu giữa các lần gửi thành công cùng MAC mặc định 20 giây, có thể đổi qua cấu hình. Đây không phải SLA hoàn tất.

Worker đối soát chạy khoảng mỗi 25 giây và chỉ xét tag Uploaded. Nó kiểm tra `STATUS` thuộc 0/10, `IMAGE_ID > 0`, `IMAGE_ID == IMAGE_ID_LOCAL`. Hiện chưa ràng buộc ảnh đó với revision/job mới nhất hoặc `IMAGE_FILE`; do đó Confirmed chưa đủ làm bằng chứng độc lập rằng chính nội dung yêu cầu mới đã hiển thị. Nghiệm thu cần đối chiếu lần gửi và quan sát tag.

API TagDetailDto không trả `DesiredRevision`, `ConfirmedRevision` hoặc `LastConfirmedAt`; không dựa vào các trường chưa có trong response. `lastSyncedAt` hiện được gán bằng giờ UTC của backend khi worker đánh dấu Confirmed, không phải timestamp RF do tag báo về. Thời điểm upload nằm ở LastUploadedAt trong database và không có trong DTO này.

Gọi GET để theo dõi với chu kỳ phù hợp (ví dụ 5 giây) và timeout do MES đặt theo yêu cầu vận hành. Timeout theo dõi không tự hủy job trên backend. Đừng POST liên tục để thay cho polling.

## 4. Gửi lại và theo dõi hàng đợi

```powershell
$mac = 'D0C00001'
$job = Invoke-RestMethod -Method Post "$base/api/Dispatch/send-tag/$mac"
$queue = Invoke-RestMethod "$base/api/Dispatch/status"
```

HTTP **202**:

```json
{
  "message": "Tag 'D0C00001' enqueued for rendering and dispatching.",
  "jobId": "11111111-1111-1111-1111-111111111111",
  "mac": "D0C00001",
  "model": "MODEL-TEST",
  "state": 3,
  "queuePending": 1
}
```

JobId dùng tra log/database. `queuePending` là snapshot số job Pending; worker có thể lấy job ngay nên số này có thể bằng 0. `GET /api/Dispatch/status` trả `pendingJobs` = Pending + Dispatching và `timestamp` UTC; không tính Uploaded đang chờ xác nhận.

`send-tag` nhận `?newStateCode=3` ở query string, không phải JSON body. Endpoint này hiện không kiểm tra mã trạng thái tồn tại giống luồng MES; ưu tiên API đổi trạng thái theo máy để có validation nghiệp vụ. Không dùng mã không tồn tại để thử trên database thật.

`POST /api/Dispatch/send-all` tạo job cho mọi tag, trả 202 với `message`, `totalTags`, `queuePending`. Trường queuePending của endpoint này bằng số tag vừa được tạo job, không phải tổng hàng đợi thực tế. Gọi lặp có thể tạo thêm job.

Mỗi lần gọi đổi trạng thái (kể cả mã không đổi) tăng revision và tạo job, đồng thời thay các job Pending cũ. Job đang Dispatching có thể vẫn chạy. Không có cam kết exactly-once; nếu request timeout, đọc lại trạng thái trước khi quyết định gửi lại.

## 5. Lỗi và Content-Type

| Trường hợp | HTTP | Body thường gặp | Cách xử lý |
| --- | --- | --- | --- |
| Máy/MAC không tồn tại | 404 | ApiResponse, application/json | Kiểm tra binding; không tự retry cùng thông tin |
| StateCode ngoài 0–999 hoặc sai kiểu | 400 | ValidationProblemDetails, application/problem+json | Sửa payload |
| StateCode trong khoảng nhưng không có trong danh mục | 400 | ApiResponse, application/json | GET danh mục lại |
| JSON lỗi cú pháp | 400 | ValidationProblemDetails | Sửa JSON |
| Content-Type không hỗ trợ | 415 | ProblemDetails | Gửi application/json |
| Lỗi chuỗi từ Dispatch/Render | 400/404 | text/plain hoặc JSON string tùy negotiation | Đọc status và nội dung, không ép thành ApiResponse |
| Test Connection thất bại | 200 | SftpTestResult với success=false | Đọc message để phân biệt kết nối/xác thực |
| Lỗi server chưa xử lý | 500 | Không cam kết schema thống nhất; môi trường production có error handler trang | Thu log, không giả định luôn là JSON |

Ví dụ lỗi nghiệp vụ:

```json
{ "success": false, "message": "Machine state code '999' is invalid.", "data": null }
```

ValidationProblemDetails chứa `type`, `title`, `status`, `errors`, có thể có `traceId`; message và đường dẫn key thay đổi theo lỗi. Trong OpenAPI, response 400 mô tả cả validation và kiểu lỗi nghiệp vụ bằng `anyOf` khi cần, vì các schema có thành viên tùy chọn và có thể chồng lấp. Mọi lỗi chưa được chuyển sang một dạng ProblemDetails chung.

## 6. Cấu hình kết nối và kiểm tra

```powershell
$settings = @{
    host = '<EBS50_IP>'
    port = 22
    username = '<SSH_USER>'
    password = '<SSH_PASSWORD>'
    remotePath = '/home/root/ebs_50_run/Input'
} | ConvertTo-Json
Invoke-RestMethod -Method Post "$base/api/Dispatch/config" -ContentType 'application/json' -Body $settings
$result = Invoke-RestMethod -Method Post "$base/api/Dispatch/test-connection"
$result | Select-Object success, message, host, port, remotePath
```

Việc lưu cấu hình không tự thử kết nối. Test Connection kiểm tra SSH/SFTP và Input tồn tại; nếu Input thiếu, hiện `success` vẫn có thể là true với cảnh báo trong message. Phép thử không ghi file và không xác nhận radio.

GET `/api/Dispatch/config` trả cả mật khẩu đang lưu. Không lưu response đó vào log MES hoặc chia sẻ screenshot. Thứ tự ưu tiên cấu hình và khác biệt fallback xem [README](../README.md).

## 7. Các API quản trị khác

Swagger là danh mục chi tiết 25 operation: Tags (liệt kê, đọc, đổi trạng thái, link, xóa), Machines (đọc tag/đổi trạng thái), Models (liệt kê, lưu, xóa), States (CRUD và icon), Dispatch (gửi, queue, cấu hình, kiểm tra), Render (preview/simulate).

- `POST /api/Tags/link`: request có `macAddress`, `machineNo`, `modelCode`, `initialStateCode`, `autoDispatch`. Hiện `autoDispatch=false` chỉ bỏ thông báo đánh thức; service vẫn tạo job và worker polling có thể gửi. Không dùng tùy chọn này làm chế độ “không gửi”.
- `POST /api/States` và `POST /api/States/{stateCode}/icon` dùng multipart/form-data, không phải JSON cho file icon.
- Preview `/api/Render/preview/{mac}` và `/api/Render/simulate?model=MODEL-TEST&state=3&color=Red` trả image/png; không gửi xuống tag.
- Liên kết backend không phải đồng nghĩa có dòng links trên trạm: luồng hiện tại dùng XML External CMS để xử lý ảnh/liên kết ở phía trạm.

## 8. Kiểm chứng khi bàn giao MES

Thử trên database/trạm thử: một mã máy tồn tại, một mã không tồn tại, JSON sai, state không hợp lệ, kết nối SFTP không thành công, lệnh 200/202 rồi GET theo dõi. Lưu request, HTTP status, response và thời điểm; che thông tin đăng nhập.

Để nghiệm thu phần cứng, ghi thêm MAC, nội dung ảnh mong đợi, JobId/log upload, dữ liệu trạm sau gửi và ảnh chụp màn hình tag. Đợt tài liệu này chưa làm bước vật lý; kết quả kiểm thử API và SQL nằm ở [VERIFICATION.md](VERIFICATION.md).
