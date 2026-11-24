# CI — Hủy lượt PR bị thay thế

Ngày quyết định: **25/11/2026**. Owner yêu cầu thực hiện mục 3 của kế hoạch giảm thời gian chờ CI, sau thay đổi bốn worker APP-00A đã merge ở [PR #145](https://github.com/chung6a8m/fmcg-distribution-dotnet/pull/145).

Baseline nguồn: `dfa59378c871cbe8fcfcdaaf92c5f3bbdd5bcefd`. Phạm vi là điều phối CI; không thay đổi acceptance, application, runner script hoặc phạm vi nghiệm thu vật lý.

## Chính sách

Tất cả **16 workflow có trigger `pull_request`** dùng cùng cấu trúc ở cấp workflow:

```yaml
concurrency:
  group: ${{ github.workflow }}-${{ github.event_name }}-${{ github.event.pull_request.number || github.run_id }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}
```

| Trường hợp | Hành vi |
| --- | --- |
| Cùng workflow, cùng số PR, commit mới | Lượt mới vào cùng group và yêu cầu hủy lượt cũ đang chạy; lượt pending cũ cũng có thể bị thay thế |
| Khác PR, kể cả hai fork có cùng tên nhánh | Số PR khác nhau tạo group khác nhau |
| Khác workflow trong cùng PR | Tên workflow khác nhau tạo group khác nhau |
| Các push lên `main` | Không có PR number nên dùng run ID riêng; cancellation tắt |
| Các lần `workflow_dispatch` | Dùng run ID riêng; cancellation tắt |

Các tên workflow hiện tại là duy nhất kể cả khi so sánh không phân biệt hoa/thường. Số PR được dùng thay cho tên nhánh; SHA và `run_attempt` không được đưa vào phần định danh PR vì sẽ tách những lượt cần thay thế nhau.

Run ID riêng cho push/manual tránh cả hai tác động: hủy lượt đang chạy và thay thế một lượt đang pending. Chỉ đặt `cancel-in-progress: false` trong một group dùng chung cho `main` không đủ để tránh tác động thứ hai. Nếu thêm loại event mới có payload PR, phải review lại cách chọn định danh trước khi mở rộng chính sách.

## Phạm vi workflow và bảo toàn kiểm chứng

| Nhóm | Workflow | Job khi cùng được kích hoạt |
| --- | ---: | ---: |
| PR không lọc đường dẫn và push `main` | 10 | 24 |
| PR có bộ lọc đường dẫn và manual: LAB-D02/D03/D04/E05/I01, B9 | 6 | 8 |
| Tổng | 16 | 32 |

Các bộ lọc đường dẫn và trigger manual hiện có được giữ nguyên. Chỉnh sửa chính file workflow của sáu nhóm có filter sẽ kích hoạt kiểm chứng synthetic/source tương ứng trong PR này; đây không phải quyền chạy LAB host Apply hoặc bằng chứng physical PREPROD.

Concurrency đặt ở cấp **workflow**, nên bốn worker APP-00A và các job matrix vẫn chạy song song trong một lượt. Tên check `validate`, `needs`, `if: always()`, gate yêu cầu đủ bốn kết quả `success`, quyền, timeout, phiên bản SDK/runtime, lệnh test và artifact paths đều giữ nguyên.

## Đọc kết quả hủy và chạy lại

- GitHub xử lý theo lúc lượt chạy vào group; cơ chế này không so sánh tuổi commit và không bảo đảm thứ tự commit trong mọi race.
- Chỉ re-run lượt của **HEAD hiện tại** khi cần xử lý lỗi hạ tầng. Re-run một lượt PR cũ giữ nguyên SHA/event ban đầu và có thể hủy lượt HEAD mới hơn trong cùng group.
- Hủy không đồng nghĩa mọi tiến trình dừng tức thì. Các bước thu evidence và job có `if: always()` có thể tiếp tục xử lý kết thúc. Artifact của lượt bị hủy có thể không đầy đủ.
- Lượt bị thay thế không được tính là acceptance thành công. Kết luận phải dựa trên CI và evidence của HEAD cuối; giữ lịch sử `cancelled` tách biệt với lỗi assertion hoặc bootstrap.
- Chính sách không đưa các lượt đã tạo từ workflow cũ, chưa có concurrency, vào group mới. Không diễn giải thay đổi này thành đã hủy hồi tố mọi lượt lịch sử.

## Kiểm chứng và biên nhận

Kiểm tra cấu trúc phải xác nhận cả 16 workflow chỉ thêm mapping `concurrency`: mọi phần khác của YAML, gồm job, trigger/path filter và quyền, giống baseline. Chạy actionlint, `git diff --check` và 15 test gate APP-00A hiện có.

Kiểm chứng trực tiếp dùng hai commit có nội dung: A gồm policy và kế hoạch, B gồm hướng dẫn vận hành này. Cả hai đều có policy. Sau khi CI của A đã bắt đầu, cập nhật PR lên B và quan sát GitHub tự hủy các lượt bị thay thế. Không dùng thao tác hủy thủ công làm bằng chứng cho tính năng tự hủy. Ghi riêng lượt đã hoàn tất trước B, lượt bị thay thế khi pending và lượt bị hủy khi đang chạy.

Biên nhận trong PR cần ghi chính xác SHA A/B, run/attempt, trạng thái và thời điểm trước/sau, thông báo hủy nếu có, cùng kết quả 16 workflow/32 job trên B. Hai commit chỉ chứng minh tình huống A → B đã quan sát; phân tách PR/workflow/main còn được đối chiếu từ cấu hình, không gán thành thử nghiệm runtime của mọi tổ hợp. Lỗi hạ tầng hoặc retry phải được giữ trong biên nhận. Không tạo commit mới chỉ để ghi nhận lượt CI vừa hoàn tất.

Tài liệu nền: [APP-00A song song](app-00a-parallel-ci.md). Kế hoạch: [PR CI concurrency](../superpowers/plans/2026-11-25-pr-ci-concurrency.md).

Tham chiếu chính thức: [GitHub concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency), [workflow cancellation](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-cancellation), [reruns](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs).
