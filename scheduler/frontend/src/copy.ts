export const COPY = {
  product: {
    eyebrow: "VNU · UET",
    title: "Lập kế hoạch thời khóa biểu",
    currentFile: "Thời khóa biểu nguồn",
    noFile: "Chưa chọn",
  },
  steps: {
    upload: "Nạp dữ liệu",
    selection: "Chọn môn",
    results: "Tìm lịch",
    navigationLabel: "Quy trình lập lịch",
  },
  theme: {
    label: "Giao diện",
    system: "Theo hệ thống",
    light: "Sáng",
    dark: "Tối",
  },
  accessibility: {
    skipNavigation: "Bỏ qua phần điều hướng",
  },
  upload: {
    kicker: "Bước 1 · Thời khóa biểu",
    heading: "Chọn thời khóa biểu",
    description: "Chọn file thời khóa biểu để bắt đầu.",
    inputLabel: "Chọn hoặc thả thời khóa biểu",
    emptyPrompt: "Chọn hoặc thả thời khóa biểu",
    supportedFormats: (desktopApp: boolean) =>
      desktopApp ? "Hỗ trợ XLSX và PDF" : "Hỗ trợ XLSX · PDF sẽ bổ sung",
    readyTitle: "Sẵn sàng đọc",
    loadingTitle: "Đang đọc…",
    loadingDetail: "Đang kiểm tra các môn học và giảng viên.",
    readButton: "Đọc thời khóa biểu",
    missingTitle: "Chưa chọn thời khóa biểu",
    missingDetail: "Chọn một file thời khóa biểu trước khi tiếp tục.",
    invalidTitle: "File chưa được hỗ trợ",
    invalidDetail: (desktopApp: boolean) => desktopApp
      ? "Chỉ nhận file XLSX hoặc PDF. Hãy chọn lại thời khóa biểu."
      : "Hiện tại chỉ đọc được file XLSX. Hãy chọn lại thời khóa biểu.",
    errorTitle: "Không thể đọc thời khóa biểu",
    successTitle: "Đã đọc thời khóa biểu",
    successDetail: (anchorCount: number, sessionCount: number) =>
      `${anchorCount} cặp môn và giảng viên · ${sessionCount} buổi có lịch cố định.`,
  },
  selection: {
    kicker: "Bước 2 · Chọn môn",
    heading: "Chọn môn và nhóm giảng dạy",
    description: "Chọn các môn bạn muốn học và nhóm giảng dạy phù hợp.",
    courseLabel: "Môn học",
    coursePlaceholder: "Tìm theo mã hoặc tên môn…",
    courseResultsLabel: "Môn học phù hợp",
    noCourseResults: "Không tìm thấy môn phù hợp.",
    rowLabel: (index: number) => `Môn học ${index}`,
    lecturerLabel: "Nhóm giảng dạy",
    lecturerPlaceholder: "Chọn nhóm giảng dạy",
    incomplete: "Chọn đủ môn và nhóm giảng dạy trước khi tiếp tục.",
    addCourse: "Thêm môn",
    removeCourse: (index: number) => `Bỏ môn ${index}`,
    removeButton: "Bỏ môn",
    continue: "Tiếp tục",
    summaryLabel: "Các môn đã chọn",
    summaryHeading: "Đã chọn",
    emptySummary: "Chọn đủ môn và nhóm giảng dạy để tiếp tục.",
    changedTitle: "Lựa chọn đã thay đổi",
    changedDetail: "Kết quả trước đó không còn áp dụng cho lựa chọn hiện tại.",
    lecturerCount: (count: number) => `${count} nhóm giảng dạy`,
    validationWarning: (count: number) =>
      `Thời khóa biểu có ${count} điểm cần kiểm tra. Hệ thống vẫn giữ nguyên các buổi đã đọc.`,
    validationDetail: "Bạn vẫn có thể chọn lớp từ dữ liệu cố định; các cảnh báo không thay đổi file gốc.",
    skippedRows: (count: number) => `${count} dòng chưa được đưa vào lịch do thiếu hoặc sai thông tin bắt buộc.`,
    partialImport: (count: number) => `Đã tạm loại ${count} LHP có lịch chưa công bố. Các LHP hoàn chỉnh vẫn có thể được chọn.`,
    partialImportDetails: "LHP tạm loại:",
    fatalRows: "Một số dòng lịch chưa đủ dữ liệu nên chưa thể tìm lịch. Hãy chọn lại file đã hoàn chỉnh.",
    fatalRowDetails: "Các dòng cần sửa:",
  },
  solve: {
    kicker: "Bước 3 · Tìm lịch",
    heading: "Tìm thời khóa biểu",
    description: "Chọn thời gian tìm tối đa rồi bắt đầu.",
    fixedScheduleNote: "Hệ thống chỉ chọn các lớp có sẵn trong thời khóa biểu.",
    timeoutLegend: "Thời gian tìm tối đa",
    presets: {
      quick: "Nhanh",
      balanced: "Mặc định",
      thorough: "Lâu hơn",
      quickNote: "30 giây",
      balancedNote: "3 phút",
      thoroughNote: "5 phút",
    },
    customTimeout: (min: number, max: number) => `Tùy chỉnh (${min}–${max} giây)`,
    back: "Quay lại chọn môn",
    start: "Tìm thời khóa biểu",
    busyStart: "Đang tìm lịch…",
    busyDetail: "Đang xét các phương án phù hợp.",
    cancel: "Dừng tìm kiếm",
    cancelling: "Đang dừng…",
    cancelledTitle: "Đã dừng tìm kiếm",
    cancelledDetail: "Bạn có thể đổi lựa chọn rồi thử lại.",
    cancellingDetail: "Đang dừng tìm kiếm.",
    summaryLabel: "Các môn sẽ tìm",
    summaryHeading: (selected: number, total: number) => `Đã chọn ${selected}/${total} môn`,
    emptySummary: "Chưa có môn nào được chọn.",
    invalidTitle: "Chưa đủ lựa chọn",
    invalidDetail: "Chọn đủ môn và giảng viên trước khi tìm lịch.",
    missingTitle: "Chưa có thời khóa biểu",
    missingDetail: "Chọn thời khóa biểu trước khi tìm lịch.",
    errorTitle: "Không thể tìm thời khóa biểu",
  },
  results: {
    kicker: "Kết quả",
    heading: "Kết quả",
    busyHeading: "Đang tìm lịch…",
    busyDetail: "Đang xét các phương án phù hợp.",
    busyProgressLabel: "Đang tìm lịch",
    busyProgressText: "Đang xét các phương án",
    noResultHeading: "Chưa có kết quả",
    noResultDetail: "Quay lại chọn môn hoặc thử lại.",
    validHeading: "Đã tìm thấy lịch",
    noScheduleHeading: "Chưa có lịch phù hợp",
    uncertainHeading: "Chưa xác định được kết quả",
    noScheduleDetail: "Các môn đang trùng lịch hoặc không có lớp phù hợp. Hãy đổi môn hoặc giảng viên.",
    timeoutTitle: "Không tìm được lịch trong thời gian cho phép",
    timeoutDetail: "Thử bớt môn hoặc tăng thời gian tìm.",
    uncertainDetail: "Thử lại hoặc xem ghi chú bên dưới.",
    emptyHeading: "Không có lịch để hiển thị.",
    emptyDetail: "Thử bớt môn, đổi giảng viên hoặc tăng thời gian tìm.",
    summaryStatus: "Kết quả",
    summaryTime: "Thời gian tìm",
    summaryCount: "Số phương án",
    solutionsLabel: "Các phương án",
    solution: (index: number) => `Phương án ${index}`,
    movement: (cost: number) => `Di chuyển ${cost}`,
    firstSolutionDetail: (count: number, cost: number) => `${count} phương án · phương án đầu tiên có ${cost} điểm di chuyển.`,
    railLabel: "Thông tin phương án",
    emptyRail: "Chưa có phương án.",
    onlineLabel: "Buổi học trực tuyến",
    online: "Trực tuyến",
    weekLabel: "Thời khóa biểu theo tuần",
    weekGridLabel: "Lịch trong tuần",
    mobileLabel: "Lịch theo ngày",
    detailLabel: "Chi tiết môn học",
    detailHeading: "Lớp học phần đã chọn",
    movementLabel: "Điểm di chuyển",
    notesLabel: "Ghi chú",
    changeSelection: "Đổi lựa chọn",
    chooseAnotherFile: "Chọn thời khóa biểu khác",
    exportUnsat: "Xuất gói kiểm chứng UNSAT",
    partialUnsatDetail: "Kết quả này chỉ áp dụng cho các LHP đã đủ dữ liệu; không thể xuất chứng nhận formal khi file còn LHP chưa công bố lịch.",
    exportingUnsat: "Đang xuất gói…",
    exportedUnsatTitle: "Đã xuất gói kiểm chứng",
    exportedUnsatDetail: (fileName: string) => `Đã lưu ${fileName}. Bạn có thể dùng các lệnh trong gói để kiểm tra hình thức.`,
    exportCancelled: "Đã hủy xuất gói.",
    exportErrorTitle: "Không thể xuất gói",
    exportErrorDetail: "Hãy thử chọn vị trí lưu khác.",
    elapsed: (elapsedSeconds: number, timeoutSeconds: number) =>
      `Đã chạy ${elapsedSeconds} giây · tối đa ${timeoutSeconds} giây`,
    weekPeriodLabel: "Ca",
    roomLabel: "phòng",
  },
} as const;

const ERROR_PATTERNS: Array<[RegExp, string]> = [
  [/invalid result|invalid response|protocol version/i, "Ứng dụng trả dữ liệu không hợp lệ. Hãy cập nhật ứng dụng rồi thử lại."],
  [/sheet|worksheet|workbook|xlsx|excel/i, "Không thể đọc thời khóa biểu. Hãy kiểm tra file XLSX rồi thử lại."],
  [/timed out|timeout|time limit/i, "Tìm lịch mất quá nhiều thời gian. Hãy thử bớt môn hoặc tăng thời gian tìm."],
  [/no compatible|no course|candidate|teaching-team request|selected requests/i, "Không tìm được lớp phù hợp cho các môn đã chọn."],
  [/solver|solverworker/i, "Không thể tìm thời khóa biểu. Hãy thử lại hoặc kiểm tra cài đặt ứng dụng."],
  [/bridge|webview|native worker|desktop/i, "Không thể mở giao diện. Hãy thử lại."],
];

export function userFacingError(error: unknown, fallback: string): string {
  const raw = error instanceof Error ? error.message : "";
  return ERROR_PATTERNS.find(([pattern]) => pattern.test(raw))?.[1] ?? fallback;
}

export function userFacingNote(note: string): string | null {
  if (/returned \d+ feasible/i.test(note)) {
    return null;
  }
  if (/no compatible personal timetable/i.test(note)) {
    return "Không có phương án nào phù hợp với các môn đã chọn.";
  }
  if (/timed out/i.test(note)) {
    return "Tìm lịch đã hết thời gian cho phép.";
  }

  const hardConstraintMessages: Array<[string, string]> = [
    ["HC-1", "Có buổi học trùng phòng."],
    ["HC-2", "Có buổi học trùng giảng viên."],
    ["HC-3", "Có buổi học trùng nhóm sinh viên."],
    ["HC-4", "Có xung đột với lịch giảng viên khác khoa."],
    ["HC-5", "Có buổi học trùng trong cùng lớp học phần."],
    ["HC-6", "Có phòng chưa phù hợp với loại buổi học."],
  ];
  return hardConstraintMessages.find(([code]) => note.includes(code))?.[1] ?? null;
}
