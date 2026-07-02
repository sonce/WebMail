namespace WebMail.Domain;

public enum UserRole { Administrator = 1, Sales = 2, Supplier = 3 }
public enum EmailAuthorizationStatus { NotAuthorized = 1, Authorized = 2, Abnormal = 3 }
public enum BuyerStage { NotSent = 1, Sent = 2, Opened = 3, Submitted = 4 }
public enum ReviewStatus { Pending = 1, Approved = 2, Rejected = 3 }
public enum SupplierProcessingStatus { Unprocessed = 1, Failed = 2, Completed = 3 }
public enum MailFolder { Inbox = 1, Junk = 2 }

// 发送给销售时如何处理每张卡的免审核标记
public enum AutoApproveAction
{
    NoChange = 1,       // 不修改：保留每张卡各自的 AutoApprove
    AutoApprove = 2,    // 免审核：整批置 true
    RequireReview = 3   // 需审核：整批置 false
}
