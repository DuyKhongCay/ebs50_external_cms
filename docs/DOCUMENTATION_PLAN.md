# Kế hoạch bổ sung tài liệu ebs50_backend

Ngày lập: 18/09/2026. Trạng thái: đã triển khai tài liệu, metadata OpenAPI và kiểm chứng ngoại tuyến. Nghiệm thu tag thật còn chưa thực hiện; xem [báo cáo kiểm chứng](VERIFICATION.md). Đây là bản kế hoạch có cập nhật kết quả, không phải hướng dẫn thao tác trực tiếp trên trạm.

## 1. Mục tiêu và phạm vi

Hoàn thiện tài liệu để lập trình viên chạy được backend, người tích hợp MES gọi đúng API và kỹ thuật viên cấu hình, chẩn đoán, khôi phục trạm EBS50 theo quy trình có thể kiểm chứng.

Ưu tiên đầu tiên là lưu lại cách tắt Layering đã xác minh trên trạm. Tiếp theo là hướng dẫn bắt đầu, tích hợp API và mở rộng tài liệu vận hành hiện có.

Các đầu ra dự kiến:

| Ưu tiên | Đầu ra | Đối tượng | Trách nhiệm nội dung |
| --- | --- | --- | --- |
| P0 | `docs/EBS50_CONFIGURATION.md` | IT, kỹ thuật viên vận hành | Cấu hình trạm, External CMS, Layering, sao lưu và khôi phục trạm |
| P1 | `README.md` | Người mới tiếp nhận project | Tổng quan, yêu cầu môi trường, cách chạy, đường dẫn tới tài liệu chi tiết |
| P1 | `docs/MES_API_GUIDE.md` | Lập trình viên MES | Quy trình tích hợp, hợp đồng API, ví dụ, lỗi và theo dõi kết quả |
| P1 | Metadata OpenAPI trong mã nguồn | Người dùng Swagger và công cụ tích hợp | Request/response, summary, mã HTTP, định danh operation |
| P1 | Cập nhật `docs/OPERATIONS_GUIDE.md` | Người triển khai và trực vận hành | Triển khai Windows Service, theo dõi luồng gửi, xử lý sự cố |

Đợt này tập trung vào tài liệu và metadata API. Các thay đổi hành vi như nâng framework, chuyển Controllers sang Minimal API, thay định dạng lỗi, thêm cơ chế xác thực hoặc sửa thuật toán xác nhận ảnh sẽ được ghi thành công việc riêng nếu phát hiện cần thiết.

Không cần tạo thêm một tài liệu kiến trúc độc lập ở đợt đầu: đưa sơ đồ tổng quan vào README, phần luồng xử lý chi tiết vào hướng dẫn vận hành để tránh trùng nội dung.

## 2. Hiện trạng khi lập kế hoạch và căn cứ

### 2.1. Những gì đã có trong repository

- Project dùng .NET 8, Razor Pages và API Controllers.
- Swagger được đăng ký bằng Swashbuckle; cấu hình đường dẫn `/swagger` và `/swagger/v1/swagger.json` nằm trong `Program.cs`.
- Nhiều controller có XML comments và `ProducesResponseType`, nhưng cấu hình được đọc chưa bật `GenerateDocumentationFile` và chưa nạp XML comments vào Swagger.
- `docs/OPERATIONS_GUIDE.md` đã mô tả cài đặt, Windows Service, cổng mặc định 6789, kết nối SFTP và sao lưu database backend.
- Có các script publish, cài/gỡ service, tạo installer. Các thao tác triển khai trong tài liệu phải đối chiếu các script này.
- Chưa có README tại gốc project và chưa có hướng dẫn riêng cho MES hoặc sự cố Layering.

### 2.2. Những gì đã xác minh trên trạm trong phiên xử lý sự cố

- Dịch vụ trên trạm: `application.service`; thư mục ứng dụng: `/home/root/ebs_50_run`.
- Database đã kiểm tra: `/home/root/ebs_50_run/Output/esl.sqlite3`.
- Phiên bản controller ghi trong log: `1.0.80.9575`; đường dẫn user.config đã quan sát nằm dưới phiên bản ứng dụng `3.0.11.9577`.
- Chỉ đặt `Layering=False` trong user.config không giữ được cấu hình sau khởi động khi bảng `links` vẫn có cột `Layer`.
- Mã thư viện điều khiển được kiểm tra cho thấy `ReloadLinks()` gọi `EnableLayering()` khi cấu hình và sự hiện diện của cột `Layer` không khớp.
- Hai bảng `links` và `links_staging` đều trống khi thực hiện chuyển đổi cấu trúc không có `Layer`.
- Sau khi đồng bộ cấu trúc bảng và cấu hình, dịch vụ chạy lại, `Layering=False` được giữ, kiểm tra toàn vẹn SQLite trả về `ok`, log khởi động mới không có lỗi `Layer`.
- Chưa thực hiện gửi ảnh thử và xác minh màn hình tag sau thay đổi này. Trạm báo `Connected` không đủ để kết luận tag đã nhận ảnh.
- Đồng hồ trạm lúc kiểm tra hiển thị tháng 04/2026, trong khi máy tính hiển thị tháng 09/2026. Tên backup theo giờ trạm không được diễn giải thành thời điểm thực tế của sự cố.

Tài liệu phải phân biệt dữ kiện đã xác minh trên phiên bản này với hướng dẫn còn cần kiểm chứng trên các phiên bản khác.

## 3. Nguyên tắc biên soạn và kiểm chứng

1. Viết tiếng Việt, giữ nguyên tên trường, đường dẫn, mã trạng thái và tên lệnh.
2. Mỗi quy trình có điều kiện đầu vào, các bước, kết quả mong đợi, dấu hiệu thất bại và cách khôi phục.
3. Phân biệt lệnh PowerShell trên Windows với lệnh shell chạy qua SSH trên EBS50.
4. Dùng địa chỉ, MAC, máy và mật khẩu mẫu dạng placeholder trong hướng dẫn dùng lại; không đưa thông tin đăng nhập thực vào ví dụ.
5. Mỗi chủ đề chỉ có một nơi mô tả chi tiết; các tài liệu khác liên kết tới nơi đó.
6. Kiểm chứng ví dụ trên môi trường kiểm thử trước; ghi rõ bước cần phần cứng và trạng thái chưa kiểm thử khi chưa có kết quả.
7. Đối chiếu với hành vi thực tế, không lấy comment trong mã nguồn làm bằng chứng duy nhất. Ví dụ, `TestConnectionAsync` hiện kiểm tra kết nối và sự tồn tại của thư mục, chưa thử ghi file.
8. Ghi rõ thay đổi metadata OpenAPI và thay đổi hành vi API. Không âm thầm đổi HTTP status hoặc response để làm tài liệu đồng nhất.

## 4. Giai đoạn 0 — Chốt thông tin nền

**Công việc**

- Kiểm kê phiên bản framework, thư viện Swagger, route, DTO, mã HTTP và cấu hình serialize JSON.
- Đối chiếu thứ tự ưu tiên cấu hình của SFTP, SSH đọc trạng thái và API đọc/lưu cấu hình; không mặc định mọi thành phần có cùng giá trị fallback.
- Xác định đường dẫn database, Assets, bản XML/PNG lưu cục bộ và thư mục log trong từng cách chạy: phát triển, publish, Windows Service.
- Đối chiếu installer và script với hướng dẫn vận hành hiện có: tên service, thư mục cài, cổng, quyền ghi, cách cập nhật và bảo toàn database.
- Ghi phiên bản trạm và cách tìm đúng user.config; không cố định chuỗi hash hoặc số phiên bản trong hướng dẫn dùng lại.
- Lập danh sách điểm chưa kiểm chứng, đặc biệt mã RF, khả năng phát hiện đúng revision, thao tác trên database có dữ liệu và hiệu lực của cấu hình qua giao diện trạm.

**Nguồn chính**: `Program.cs`, `ebs50_backend.csproj`, `Controllers/`, `DTOs/`, `Data/`, `Services/Dispatching/`, `scripts/`, `installer/`, cấu hình và log trạm đã thu thập.

**Nghiệm thu**: có danh sách thông tin đã xác nhận và các giới hạn; mọi đường dẫn, tên service, route được dùng trong bản thảo có nguồn đối chiếu.

## 5. Giai đoạn 1 — Tài liệu cấu hình EBS50 (P0)

Tạo `docs/EBS50_CONFIGURATION.md` theo cấu trúc dưới đây.

### 5.1. Chuẩn bị và nhận diện môi trường

- Địa chỉ trạm, tài khoản SSH, phiên bản ứng dụng/controller và quyền truy cập cần có.
- Phân biệt `etag_database.db` trên Windows và `Output/esl.sqlite3` trên EBS50.
- Sơ đồ thư mục `Input`, `Output/Processed`, database và user.config.
- Cách phát hiện đúng file cấu hình đang sử dụng và kiểm tra các tiến trình trước thao tác.
- Cấu hình External CMS qua giao diện: chỉ ghi tên màn hình, URL và bước bấm đã đối chiếu phiên bản thực tế.

### 5.2. Giải thích nguyên nhân Layering

- Trình bày triệu chứng `Error linking: Column 'Layer' does not allow nulls.` cùng vị trí tra log.
- Giải thích quan hệ giữa `Layering`, cột `Layer` và khóa chính của hai bảng liên kết.
- Ghi rõ ứng dụng có thể tự bật lại Layering khi cấu hình và cấu trúc bảng không khớp.
- Không hướng dẫn thêm `<Layer>` vào XML External CMS để chữa lỗi: phiên bản đã kiểm tra báo `Unknown Node: 'Layer'` với cách này.
- Không khái quát rằng mọi hệ thống External CMS đều phải tắt Layering; giới hạn hướng dẫn theo luồng gửi ảnh và phiên bản đã kiểm tra.

### 5.3. Quy trình cho hai bảng liên kết trống

| Bước | Nội dung cần viết | Kết quả cần kiểm tra |
| --- | --- | --- |
| 1 | Kiểm tra trạm, database, schema và số bản ghi | Đúng thiết bị; cả hai bảng tồn tại và không có dữ liệu |
| 2 | Tạm ngừng nguồn gửi mới; dừng dịch vụ trạm | Không có thao tác đồng thời; tiến trình ứng dụng đã dừng |
| 3 | Sao lưu user.config và toàn bộ SQLite bằng cơ chế nhất quán | Có bản sao đọc được và kiểm tra toàn vẹn thành công |
| 4 | Kiểm tra lại số bản ghi trong transaction trước thay đổi | Nếu có dữ liệu phát sinh thì dừng thao tác, không xóa bảng |
| 5 | Chuyển hai bảng sang cấu trúc không có Layer, giữ tên/cột cần thiết | `MAC` là khóa chính; `links_staging` vẫn có cột `DELETE` |
| 6 | Đặt Layering=False ở đúng user.config khi dịch vụ đang dừng | Diff chỉ có thay đổi cấu hình dự kiến |
| 7 | Khởi động ứng dụng và chờ khởi tạo hoàn tất | Dịch vụ chạy; cấu hình vẫn False; không xuất hiện lại cột Layer |
| 8 | Kiểm tra log, toàn vẹn database và trạng thái trạm | Không có lỗi mới liên quan chuyển đổi; SQLite trả về ok |
| 9 | Gửi thử một tag được chọn, đối chiếu dữ liệu và màn hình | Có bằng chứng cập nhật từ đầu đến cuối, ghi rõ nếu chưa thử |

SQL phải có điều kiện bảo vệ bảng trống, transaction và cơ chế dừng khi lỗi. Mẫu lệnh phải được chạy thử trên database sao chép. Không đưa một lệnh DROP TABLE độc lập thành hướng dẫn sửa lỗi chung.

### 5.4. Trường hợp bảng đã có dữ liệu

- Chặn áp dụng quy trình bảng trống.
- Thống kê số liên kết, MAC trùng giữa các Layer, giá trị Layer và bản ghi staging chưa xử lý.
- Ưu tiên kiểm tra chức năng tắt Layering do ứng dụng cung cấp trên bản sao/môi trường thử; đánh giá cách chức năng đó chọn liên kết khi một MAC có nhiều lớp.
- Xác định quy tắc giữ/gộp liên kết theo nhu cầu vận hành, không tự chọn một dòng bất kỳ.
- Thiết kế migration riêng với đối chiếu dữ liệu trước/sau và phương án hoàn tác; chỉ ghi thành hướng dẫn thực thi sau khi kiểm thử.

Phạm vi nghiệm thu tối thiểu của đợt đầu là quy trình đã kiểm chứng cho bảng trống và nhánh dừng rõ ràng khi có dữ liệu. Nhánh migration có dữ liệu phải được đánh dấu chưa xác minh cho tới khi hoàn tất kiểm thử riêng.

### 5.5. Khôi phục

- Khôi phục đồng bộ database và user.config từ cùng mốc sao lưu khi dịch vụ dừng.
- Kiểm tra quyền file, SQLite, khởi động và kết nối trạm sau khôi phục.
- Ghi rõ ảnh hưởng của khôi phục tới các thay đổi phát sinh sau backup và cách ngăn nguồn gửi trong thời gian bảo trì.
- Ghi vị trí backup của ca đã xử lý như một ví dụ lịch sử, không sử dụng đường dẫn đó làm mặc định cho trạm khác.

**Nghiệm thu**: tài liệu có đủ hai nhánh bảng trống/có dữ liệu, lệnh kiểm tra và hoàn tác; quy trình trên bản sao database chạy thành công và bảo vệ được trường hợp không thỏa điều kiện. Kiểm tra phần cứng được ghi kết quả riêng.

## 6. Giai đoạn 2 — README và điểm bắt đầu (P1)

Tạo `README.md` tại gốc `ebs50_backend`.

**Nội dung**

- Mục đích ứng dụng và sơ đồ MES → backend → SFTP → EBS50 → tag, kèm chiều đọc trạng thái qua SSH.
- Yêu cầu .NET SDK cho phát triển và phân biệt với gói publish self-contained.
- Các bước restore, build, chạy; URL dashboard, Swagger và cách thay cổng.
- Cấu hình kết nối tối thiểu, thứ tự ưu tiên cấu hình sau khi đối chiếu từng thành phần.
- Vị trí dữ liệu thực tế theo cách chạy; việc đổi CurrentDirectory và neo database vào AppContext.BaseDirectory cần được kiểm tra khi chạy lần đầu.
- Cấu trúc thư mục quan trọng và liên kết tới ba hướng dẫn chi tiết.
- Giới hạn kiểm thử khi không có EBS50 hoặc tag vật lý.

**Nghiệm thu**: người mới có thể chạy backend trên môi trường thử từ hướng dẫn, mở dashboard/Swagger và xác định đúng database đang dùng. Không yêu cầu gửi hàng loạt tag để kiểm tra cài đặt.

## 7. Giai đoạn 3 — Hướng dẫn MES và OpenAPI (P1)

### 7.1. Nội dung MES_API_GUIDE.md

Mô tả quy trình lấy trạng thái hợp lệ, tìm tag gắn với máy, cập nhật trạng thái máy và theo dõi tiến trình gửi.

| API hiện có cần mô tả | Vai trò | Điểm cần giải thích |
| --- | --- | --- |
| `GET /api/States` | Lấy danh mục trạng thái | StateCode phải phù hợp dữ liệu hệ thống |
| `GET /api/Machines/{machineNo}/tag` | Tìm tag theo máy | Trường hợp máy chưa có tag |
| `POST /api/Machines/{machineNo}/state` | Luồng cập nhật chính cho MES | JSON body, HTTP 200 hiện tại và ý nghĩa đã tiếp nhận nghiệp vụ |
| `POST /api/Tags/{mac}/state` | Cập nhật trực tiếp theo MAC | Khi nào dùng thay cho cập nhật theo máy |
| `GET /api/Tags/{mac}` | Đọc trạng thái tag từ backend | Ý nghĩa SyncStatus và giới hạn dữ liệu trả về |
| `POST /api/Dispatch/send-tag/{mac}` | Yêu cầu gửi lại một tag | HTTP 202, JobId, tham số newStateCode ở query string |
| `GET /api/Dispatch/status` | Theo dõi số job chờ/đang gửi | Đây là số liệu tổng hợp, không phải API tra riêng một JobId |
| `POST /api/Dispatch/test-connection` | Kiểm tra kết nối | Đọc cả HTTP và Success trong body; chưa chứng minh quyền ghi hoặc truyền RF |

Các API quản trị còn lại được lập mục lục và dẫn tới Swagger, tránh biến hướng dẫn MES thành bản sao của toàn bộ schema API.

Ví dụ gồm PowerShell/curl và JSON thực tế; kiểm tra tên thuộc tính sau serialize, kiểu dữ liệu, body lỗi và Content-Type. Cần bao phủ máy/MAC không tồn tại, trạng thái không hợp lệ, payload sai, lỗi kết nối và trường hợp lệnh được tiếp nhận nhưng tag chưa hiển thị.

Mô tả việc lặp lại yêu cầu theo hành vi hiện tại; không khẳng định có idempotency key, SLA cập nhật hoặc endpoint tra job nếu hệ thống chưa triển khai.

### 7.2. Ý nghĩa trạng thái

- Tách trạng thái nghiệp vụ của máy (`StateCode`) khỏi trạng thái đồng bộ của tag (`SyncStatus`) và trạng thái job.
- Đối chiếu luồng `Pending → Dispatching → Uploaded → Confirmed`, các nhánh retry, `Failed`, `Error`, `Superseded` và phạm vi áp dụng của từng trạng thái.
- `Uploaded` chứng minh giai đoạn chuyển file; việc file nằm trong `Processed` chứng minh tiếp nhận/xử lý file nhưng không tự chứng minh màn hình đã đổi.
- Ghi rõ worker hiện đối chiếu mỗi 25 giây. Khoảng này là chu kỳ kiểm tra, không phải thời hạn cam kết hoàn tất cập nhật.
- Điều kiện Confirmed trong code hiện tại là trạng thái trạm thuộc 0/10, IMAGE_ID > 0 và IMAGE_ID bằng IMAGE_ID_LOCAL. Trình bày đây là điều kiện phần mềm đang dùng, không xem là chứng minh đầy đủ revision mong muốn.
- Đánh giá riêng việc ghép IMAGE_FILE/IMAGE_ID với job/revision mới nhất trước khi tài liệu cam kết xác nhận đúng nội dung của lần gửi mới. Ghi hạn chế hiện có và mở công việc sửa logic nếu cần.
- TagDetailDto hiện chưa cung cấp mọi trường revision nội bộ; không đưa các trường đó vào ví dụ response như thể API đã có.

### 7.3. Áp dụng skill aspnet-minimal-api-openapi

Áp dụng các nguyên tắc mô tả rõ request/response, validation, summary, operationId và Content-Type trên Controllers hiện có.

Các bước dự kiến:

1. Bật sinh XML documentation trong project và nạp vào Swashbuckle; xác minh file XML được đóng gói khi publish.
2. Hoàn thiện XML comments và metadata mã HTTP/schema cho các controller còn thiếu, ưu tiên DispatchController và luồng MES.
3. Xem xét DTO response đặt tên cho các anonymous response để schema dễ dùng; chỉ triển khai khi bảo toàn JSON và mã HTTP hiện tại.
4. Thiết lập operationId duy nhất, ổn định và cách nhóm API phù hợp phiên bản Swashbuckle đang dùng.
5. Ghi đúng các dạng lỗi thực tế: ApiResponse, lỗi validation hoặc chuỗi thông báo. Chuyển toàn bộ sang ProblemDetails là thay đổi hợp đồng riêng nếu muốn thực hiện.
6. Kiểm tra Swagger UI và JSON sinh ra với các trường hợp thành công/thất bại tiêu biểu.

Skill có hướng dẫn API OpenAPI tích hợp từ .NET 9 và các mẫu Minimal API như MapGroup/TypedResults. Project hiện là .NET 8 + Controllers + Swashbuckle; không lấy việc nâng nền tảng hoặc đổi kiến trúc làm điều kiện để hoàn thiện tài liệu.

**Nghiệm thu**: ví dụ MES chạy được trên môi trường thử; OpenAPI khớp request/response và mã HTTP quan sát được; build/publish với metadata mới thành công; hợp đồng API không đổi ngoài thay đổi được nêu rõ.

## 8. Giai đoạn 4 — Bổ sung vận hành và xử lý sự cố (P1)

Cập nhật `docs/OPERATIONS_GUIDE.md`, giữ phần cài đặt hiện có sau khi đối chiếu script.

**Các phần cần thêm hoặc sửa**

- Quy trình kiểm tra sau triển khai: service, dashboard, Swagger, database/Assets, kết nối trạm và một tag thử.
- Đường đi của một lần cập nhật và cách truy vết theo máy, MAC, JobId, tên file và thời gian.
- Bảng phân loại sự cố theo tầng, với lệnh kiểm tra và kết quả mong đợi.
- Phân biệt backup backend với backup trạm; dẫn tới quy trình backup/restore trạm trong EBS50_CONFIGURATION.md.
- Đối chiếu lệch giờ Windows/EBS50 khi đọc log, tránh kết luận theo thứ tự timestamp không cùng đồng hồ.
- Xử lý việc dịch vụ trạm dừng quá timeout: xác minh tiến trình đã kết thúc, ghi log và điều kiện khởi động lại, không hướng dẫn bỏ qua mọi lỗi stop.
- Kiểm tra mức log thực tế trong Windows Event Viewer; không khẳng định tất cả log Information đều có nếu provider/filter chưa được cấu hình tương ứng.

| Tầng | Ví dụ triệu chứng | Bằng chứng cần thu |
| --- | --- | --- |
| Backend | Job không chạy hoặc chờ lâu | DispatchJobs, SyncStatus, log worker, lịch retry |
| SFTP | Kết nối được nhưng chuyển file lỗi | Đường dẫn, quyền ghi thực tế, timeout và lỗi upload |
| Xử lý trên trạm | File đã được nhận nhưng liên kết thất bại | Processed, log application, schema links, cấu hình Layering |
| RF/tag | Trạm Connected nhưng tag chưa nhận ảnh | Số tag, thông tin liên lạc, IMAGE_ID/IMAGE_ID_LOCAL, quan sát thiết bị |
| Đối soát backend | Tag đã đổi ảnh nhưng backend chưa Confirmed | SSH đọc SQLite, MAC, điều kiện đối soát, thời điểm kiểm tra |

**Nghiệm thu**: người vận hành có thể xác định lỗi đang ở tầng nào, biết cần thu dữ liệu gì và truy cập được hướng dẫn khắc phục tương ứng. Không dùng Test Connection thành công làm tiêu chí duy nhất cho toàn hệ thống.

## 9. Trình tự giao việc và mốc bàn giao

| Mốc | Công việc | Phụ thuộc | Đầu ra có thể đánh giá |
| --- | --- | --- | --- |
| M0 | Kiểm kê, đối chiếu code và thông tin trạm | Không | Danh sách dữ kiện, giới hạn và nội dung cần kiểm chứng |
| M1 | Viết cấu hình trạm và Layering | M0 | EBS50_CONFIGURATION.md cùng kết quả kiểm tra trên bản sao database |
| M2 | Viết README | M0, bản thảo M1 | Hướng dẫn chạy nhanh và điều hướng tài liệu |
| M3 | Viết MES guide, hoàn thiện OpenAPI | M0 | Hợp đồng API có ví dụ đã đối chiếu và schema sinh đúng |
| M4 | Bổ sung vận hành, chẩn đoán | M1, M3 | OPERATIONS_GUIDE.md được cập nhật và liên kết chéo |
| M5 | Rà soát toàn bộ, bàn giao | M1–M4 | Bộ tài liệu nhất quán cùng kết quả kiểm chứng và phần còn chưa xác minh |

Thứ tự ưu tiên thực hiện: M0 → M1 → M2 → M3 → M4 → M5. Nếu chưa có tag thử, vẫn hoàn thành tài liệu và kiểm tra ngoại tuyến; giữ mục kiểm chứng vật lý ở trạng thái chưa thực hiện.

## 10. Ma trận kiểm chứng và tiêu chí hoàn thành

| Hạng mục | Cách kiểm chứng | Điều kiện đạt |
| --- | --- | --- |
| Markdown và liên kết | Đọc UTF-8, kiểm tra liên kết nội bộ và tên file | Không lỗi tiếng Việt, không liên kết hỏng |
| Hướng dẫn bắt đầu | Chạy theo README trên môi trường thử | Xác định đúng dữ liệu, truy cập dashboard và Swagger |
| SQL cho bảng trống | Chạy trên SQLite sao chép; kiểm tra schema và integrity | Giữ cấu trúc cần thiết, bỏ Layer đúng, không ảnh hưởng bảng khác |
| Bảo vệ dữ liệu | Thử bảng có dữ liệu hoặc nhiều lớp trên bản sao | Quy trình bảng trống từ chối thay đổi, transaction không để lại trạng thái dở dang |
| Khôi phục | Khôi phục bản sao database/config trong môi trường thử | Khôi phục được schema và dữ liệu trước đổi |
| Cấu hình bền qua khởi động | Kiểm tra sau khi ứng dụng khởi tạo xong | Layering vẫn False, schema phù hợp, log không có lỗi Layer mới |
| Hợp đồng API | Đối chiếu ví dụ và OpenAPI với response thực tế | Đúng route, tham số, body, status, Content-Type và schema |
| Metadata API | Build và kiểm tra gói publish khi có sửa code | Swagger tải được XML/schema; không đổi nghiệp vụ ngoài dự kiến |
| Toàn bộ luồng với phần cứng | Gửi một trạng thái thử, quan sát tag và đối soát | Ảnh hiển thị đúng; bằng chứng gắn với lần gửi đã chọn |

Không chạy lại thử nghiệm trực tiếp trên trạm chỉ để hoàn thiện câu chữ. Các phép thử làm thay đổi thiết bị thực hiện trong đợt kiểm chứng vận hành có phạm vi rõ ràng.

## 11. Duy trì tài liệu sau bàn giao

- README là điểm bắt đầu; OPERATIONS_GUIDE sở hữu quy trình Windows; EBS50_CONFIGURATION sở hữu cấu hình và database trạm; MES_API_GUIDE sở hữu luồng tích hợp.
- Mỗi hướng dẫn ghi ngày đối chiếu, phiên bản áp dụng và phần chưa kiểm chứng.
- Khi thay route/DTO/HTTP status, cập nhật metadata và MES guide trong cùng thay đổi.
- Khi thay đường dẫn, script cài đặt, cấu hình, schema hoặc firmware, rà lại hướng dẫn tương ứng.
- Người sửa thành phần chịu trách nhiệm cập nhật tài liệu; kết quả kiểm thử API do người tích hợp đối chiếu, kết quả phần cứng do người vận hành xác nhận.

## 12. Checklist triển khai

- [x] M0: Kiểm kê code/cấu hình; các phần firmware/UI chưa xác minh được ghi riêng.
- [x] M1: Viết EBS50_CONFIGURATION.md; kiểm thử schema fixture, bảo vệ dữ liệu và backup/restore ngoại tuyến.
- [x] M2: Viết README; xác nhận startup, đường dẫn dữ liệu tách biệt và giao diện trên bản publish.
- [x] M3: Viết MES_API_GUIDE.md; hoàn thiện metadata .NET 8, kiểm tra 25 operation và API tiêu biểu.
- [x] M4: Cập nhật OPERATIONS_GUIDE.md và liên kết chéo.
- [x] M5: Rà soát ví dụ, liên kết, định dạng và tính nhất quán; không tuyên bố đã chạy các lệnh quản trị service/trạm.
- [ ] Ghi riêng kết quả kiểm chứng trên EBS50/tag; không đánh dấu hoàn tất nếu mới chỉ upload thành công.

Các đầu ra: [README](../README.md), [cấu hình EBS50](EBS50_CONFIGURATION.md), [MES API](MES_API_GUIDE.md), [vận hành](OPERATIONS_GUIDE.md), [SQL cho bảng trống](sql/disable-layering-empty.sql). Kết quả và các công việc kỹ thuật tách khỏi phạm vi tài liệu nằm trong [VERIFICATION.md](VERIFICATION.md).
