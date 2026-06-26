namespace WebMail.Domain;

public enum UserRole { Administrator = 1, Sales = 2, Supplier = 3 }
public enum CardStatus { Unused = 1, Entered = 2, Authorized = 3, DeletedOrDisabled = 4 }
public enum EmailAuthorizationStatus { NotAuthorized = 1, PendingReview = 2, Normal = 3, Rejected = 4, Abnormal = 5 }
public enum SupplierProcessingStatus { Unprocessed = 1, Failed = 2, Completed = 3 }
public enum SyncJobStatus { Pending = 1, Running = 2, Succeeded = 3, Failed = 4 }
public enum MailFolder { Inbox = 1, Junk = 2 }
