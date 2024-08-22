# QtsBilling

## Technical Design Plan: Accounting System with POS & Inventory Management

**1. Introduction**

This document outlines the technical design for a desktop accounting system with Point of Sale (POS) and Inventory Management capabilities. The application will be developed using WinForms (.NET 8) and C# with MongoDB as the database. 

**2. System Architecture**

The system will be designed using a three-tier architecture:

* **Presentation Layer:**  WinForms application with a user-friendly interface.
* **Business Logic Layer:** C# class library containing business rules and data access logic.
* **Data Access Layer:**  MongoDB database for persistent data storage.

### Presentation Layer/ UI Layer

This is the top-most layer of the application where the user performs their activity. Let's take the example of any application where the user needs to fill up a form. This form is nothing but the Presentation Layer. In Windows applications Windows Forms are the Presentation Layer and in web applications the web form belongs to the Presentation Layer. Basically the user's input validation and rule processing is done in this layer.

### Business Layer

This is on top of the Presentation Layer. As the name suggests, most of the business operations are performed here. For example, after collecting form data we want to validate them with our custom business rule. Basically we define classes and business entities in this layer.

### Data Access Layer

On top of the Business Logic Layer is the Data Access Layer. It contains methods that help the business layer to connect with the database and perform CRUD operations. Generally all database related code and stuff belongs to the Data Access Layer. Sometimes people use a platform-independent Data Access Layer to fetch data from various database vendors.

**3. Functional Requirements**

The application will provide the following functionalities:

**User Management:**
* User Login/Logout with role-based access control (Admin, Employee, Customer).
* Manage user accounts (Add, Edit, Delete, Deactivate).

**POS (Point of Sale):**
* Process sales transactions with multiple items.
* Apply discounts and calculate taxes.
* Handle various payment methods (Cash, Credit Card).
* Generate and print receipts.

**Inventory Management:**
* Add, Edit, and Delete products with details (SKU, Name, Description, Category, Price, Cost, Inventory levels).
* Track inventory levels and generate low-stock alerts.
* Manage supplier information.
* Handle product returns and restocking.

**Accounting:**
* Manage Chart of Accounts.
* Record journal entries for all financial transactions.
* Generate financial reports (Income Statement, Balance Sheet, Cash Flow Statement).
* Manage invoices and payments.

**System Utilities:**
* Audit logging for tracking user actions and data changes.
* System configuration options (tax rates, default settings).
* Data backup and restore functionality.

* System-wide search functionality.


## Features
 * **Double-entry accounting** 
 * **Point of Sale** 
 * **Invoicing** 
 * **Billing** 
 * **Payments** 
 * **Journal Entries** 
 * **Dashboard** 
 * **Works Offline** 
 * **Financial Reports** 
 * **General Ledger** 
 * **Profit and Loss Statement** 
 * **Balance Sheet** 
 * **Trial Balance** 

**4. Technology Stack**

* **Programming Language:** C#
* **Framework:** .NET 8
* **Presentation Layer:** Windows Forms (WinForms)
* **Database:** MongoDB
* **ORM/ODM:** MongoDB.Driver (Official C# Driver)
* **Reporting:** QuestPDF(https://www.questpdf.com/)

**5. Database Design**

**Collections:**

* **Users:** _id, username, passwordHash, role, profile (firstName, lastName, phone, address), createdAt, updatedAt
* **Products:** _id, sku, name, description, category, price, cost, inventory (quantity, reorderLevel, supplier{name, contact}), createdAt, updatedAt
* **Sales:** _id, transactionId, customer{_id, name, email}, items[{productId, name, quantity, price, total}], subtotal, tax, totalAmount, paymentMethod, paymentStatus, createdAt, updatedAt
* **InventoryTransactions:** _id, productId, transactionType, quantityChange, balanceAfterTransaction, transactionDate, transactionReference 
* **Suppliers:** _id, name, contactPerson, email, phone, address, productsSupplied[{productId, name}], createdAt, updatedAt
* **Invoices:** _id, invoiceNumber, transactionId, customer{_id, name}, items[{productId, name, quantity, price, total}], subtotal, tax, totalAmount, dueDate, paymentStatus, createdAt, updatedAt
* **Payments:** _id, paymentId, invoiceId, transactionId, amount, paymentMethod, paymentDate, status, createdAt, updatedAt 
* **AuditLogs:** _id, userId, action, collection, documentId, changes{before, after}, timestamp
* **Configurations:** _id, configName, value, description, updatedAt
* **ChartOfAccounts:** _id, accountNumber, accountName, accountType, subAccount, parentAccount, description, createdAt, updatedAt
* **JournalEntries:** _id, journalEntryNumber, transactionDate, description, transactionReference, entries[{accountId, debit, credit}], createdAt, updatedAt
* **GeneralLedger (Optional):** _id, accountId, transactionDate, journalEntryNumber, description, debit, credit, balance

**Indexes:** 

Create appropriate indexes on fields frequently used in queries (e.g., `transactionId`, `productId`, `customerId`, `accountNumber`) for improved performance.

**6. User Interface Design**

* **WinForms** will be used to create a user-friendly and intuitive interface.
* Use a consistent design language and layout throughout the application.
* Implement clear navigation menus and toolbars.
* Use data grids to display data in an organized manner.
* Provide appropriate input validation and error messages to prevent data inconsistencies.

**7. Business Logic Layer Design**

* Create separate classes for each module (User Management, POS, Inventory, Accounting).
* Implement business rules and data validation within these classes.
* Use the Repository pattern or similar for data access abstraction.
* Implement robust error handling and logging.

**8. Data Access Layer Design**

* Use the official MongoDB C# Driver (`MongoDB.Driver`) for database interactions.
* Create data access classes that interact with the MongoDB collections.
* Implement methods for CRUD operations (Create, Read, Update, Delete) on each collection.
* Consider using a data mapper or similar pattern to map between domain objects and MongoDB documents.

**9. Security Considerations**

* **Authentication:** Implement secure user authentication with password hashing and salting.
* **Authorization:** Enforce role-based access control to restrict unauthorized access to features and data.
* **Data Protection:** Encrypt sensitive data like passwords and financial information stored in the database.
* **Input Validation:** Validate all user input to prevent injection attacks.

**10. Development Methodology**

* **Agile Development:** Use an iterative and incremental approach to development.
* **Version Control:** Utilize Git for source code management.
* **Testing:**  Implement unit tests for business logic and integration tests for data access and UI interactions.
* **Continuous Integration/Continuous Deployment (CI/CD):**  Set up a CI/CD pipeline to automate building, testing, and deploying the application.

**11.  Report Design & Generation with Quest PDF**

* **Report Templates:**
    * Design report layouts using Quest PDF's fluent API and C# code.  This provides flexibility for complex layouts and dynamic content.
    * Consider creating reusable layout components for consistency across reports.
* **Data Binding:**
    * Fetch data from the Business Logic Layer, structuring it into a format suitable for Quest PDF. 
    * Use Quest PDF's methods for dynamically adding tables, lists, and other elements, populating them with the fetched data. 
* **Report Features:**
    * Leverage Quest PDF features:
        * Page headers/footers, section breaks
        * Charts and graphs (using libraries like OxyPlot or ScottPlot in conjunction with Quest PDF)
        * Custom fonts and styling
* **Report Export:**
    * Generate reports in PDF format. 
    * Provide options to save reports locally or potentially send them directly to a printer.

**12. Deployment Plan**

* **Target Platform:**  Windows desktop operating systems (Windows 10/11).
* **Deployment Method:** Create an installer package (e.g., using InnoSetup or WiX) for easy installation on client machines.
* **Database Deployment:**  Deploy MongoDB on a dedicated server or consider a cloud-based solution like MongoDB Atlas.

**13. Maintenance Plan**

* **Monitoring:** Implement monitoring tools to track application performance and identify potential issues.
* **Backups:**  Schedule regular database backups to ensure data recoverability.
* **Updates:**  Release regular software updates to address bugs, security vulnerabilities, and add new features.


Designing a highly optimized NoSQL (MongoDB) database for an Accounting System with POS (Point of Sale) and Inventory Management requires a thoughtful approach to schema design, taking into account the flexibility and scalability that MongoDB offers. The key is to use embedded documents and references appropriately to balance performance with data consistency. Here's a proposed schema design:

### 1. **Users Collection**
This collection will store information about users who interact with the system, including customers, employees, and administrators.

```json
{
    "_id": ObjectId("..."),
    "username": "johndoe",
    "email": "johndoe@example.com",
    "passwordHash": "hashed_password",
    "role": "admin", // or "employee", "customer"
    "createdAt": ISODate("2024-08-11T12:00:00Z"),
    "updatedAt": ISODate("2024-08-11T12:00:00Z"),
    "profile": {
        "firstName": "John",
        "lastName": "Doe",
        "phone": "+123456789",
        "address": "123 Street Name, City, Country"
    }
}
```

### 2. **Products Collection**
This collection stores product information and tracks inventory levels.

```json
{
    "_id": ObjectId("..."),
    "sku": "PROD001",
    "name": "Product Name",
    "description": "Detailed description of the product",
    "category": "Electronics",
    "price": 49.99,
    "cost": 30.00,
    "inventory": {
        "quantity": 100,
        "reorderLevel": 10,
        "supplier": {
            "name": "Supplier Name",
            "contact": "supplier@example.com"
        }
    },
    "createdAt": ISODate("2024-08-11T12:00:00Z"),
    "updatedAt": ISODate("2024-08-11T12:00:00Z")
}
```

### 3. **Sales Collection**
This collection will store POS transactions and related information.

```json
{
    "_id": ObjectId("..."),
    "transactionId": "TXN001",
    "customer": {
        "customerId": ObjectId("..."),
        "name": "Jane Doe",
        "email": "jane.doe@example.com"
    },
    "items": [
        {
            "productId": ObjectId("..."),
            "name": "Product Name",
            "quantity": 2,
            "price": 49.99,
            "total": 99.98
        }
    ],
    "subtotal": 99.98,
    "tax": 7.50,
    "totalAmount": 107.48,
    "paymentMethod": "Credit Card",
    "paymentStatus": "Completed", // or "Pending", "Failed"
    "createdAt": ISODate("2024-08-11T12:00:00Z"),
    "updatedAt": ISODate("2024-08-11T12:00:00Z")
}
```

### 4. **Inventory Transactions Collection**
This collection tracks changes in inventory levels, such as sales, returns, and restocking.

```json
{
    "_id": ObjectId("..."),
    "productId": ObjectId("..."),
    "transactionType": "sale", // or "restock", "return"
    "quantityChange": -2,
    "balanceAfterTransaction": 98,
    "transactionDate": ISODate("2024-08-11T12:00:00Z"),
    "transactionReference": "TXN001" // Reference to the sales transaction or restock event
}
```

### 5. **Suppliers Collection**
This collection manages supplier information.

```json
{
    "_id": ObjectId("..."),
    "name": "Supplier Name",
    "contactPerson": "Supplier Contact",
    "email": "supplier@example.com",
    "phone": "+123456789",
    "address": "123 Supplier Street, City, Country",
    "productsSupplied": [
        {
            "productId": ObjectId("..."),
            "name": "Product Name"
        }
    ],
    "createdAt": ISODate("2024-08-11T12:00:00Z"),
    "updatedAt": ISODate("2024-08-11T12:00:00Z")
}
```

### 6. **Invoices Collection**
This collection tracks invoices issued for sales transactions, useful for accounting.

```json
{
    "_id": ObjectId("..."),
    "invoiceNumber": "INV001",
    "transactionId": ObjectId("..."),
    "customer": {
        "customerId": ObjectId("..."),
        "name": "Jane Doe"
    },
    "items": [
        {
            "productId": ObjectId("..."),
            "name": "Product Name",
            "quantity": 2,
            "price": 49.99,
            "total": 99.98
        }
    ],
    "subtotal": 99.98,
    "tax": 7.50,
    "totalAmount": 107.48,
    "dueDate": ISODate("2024-08-20T12:00:00Z"),
    "paymentStatus": "Paid", // or "Unpaid", "Partially Paid"
    "createdAt": ISODate("2024-08-11T12:00:00Z"),
    "updatedAt": ISODate("2024-08-11T12:00:00Z")
}
```

### 7. **Payments Collection**
This collection logs payment records for transactions.

```json
{
    "_id": ObjectId("..."),
    "paymentId": "PAY001",
    "invoiceId": ObjectId("..."),
    "transactionId": ObjectId("..."),
    "amount": 107.48,
    "paymentMethod": "Credit Card",
    "paymentDate": ISODate("2024-08-11T12:00:00Z"),
    "status": "Completed", // or "Pending", "Failed"
    "createdAt": ISODate("2024-08-11T12:00:00Z"),
    "updatedAt": ISODate("2024-08-11T12:00:00Z")
}
```

### 8. **Audit Logs Collection**
This collection stores audit logs for changes in the system, useful for compliance and security.

```json
{
    "_id": ObjectId("..."),
    "userId": ObjectId("..."),
    "action": "update",
    "collection": "products",
    "documentId": ObjectId("..."),
    "changes": {
        "before": {
            "quantity": 100
        },
        "after": {
            "quantity": 98
        }
    },
    "timestamp": ISODate("2024-08-11T12:00:00Z")
}
```

### 9. **Configurations Collection**
This collection stores system-wide configurations like tax rates, discount rules, etc.

```json
{
    "_id": ObjectId("..."),
    "configName": "taxRate",
    "value": 0.075,
    "description": "Default tax rate for sales transactions",
    "updatedAt": ISODate("2024-08-11T12:00:00Z")
}
```

### Considerations:
- **Indexes:** Ensure that you create indexes on fields that are frequently queried, such as `transactionId`, `customerId`, `productId`, and `invoiceNumber`.
- **Sharding:** If the data grows significantly, consider sharding collections like `Sales` and `Products` to distribute the load.
- **Data Consistency:** Use transactions for operations that require atomicity, such as updating inventory levels after a sale.

This schema design leverages MongoDB's strengths in handling hierarchical data and its flexibility to accommodate changes over time.

Upcoming features 

However, for a complete accounting system, consider adding these entities:

1. Chart of Accounts Collection

This is the backbone of any accounting system. It defines the categories used to organize financial transactions.

{
    "_id": ObjectId("..."),
    "accountNumber": "1000", // Follows a structured numbering system 
    "accountName": "Cash",
    "accountType": "Asset", // Asset, Liability, Equity, Revenue, Expense
    "subAccount": false, // Indicates if this is a parent account
    "parentAccount": null, // Refers to the parent account's _id if subAccount is true
    "description": "Cash on hand and in banks",
    "createdAt": ISODate("2024-08-11T12:00:00Z"),
    "updatedAt": ISODate("2024-08-11T12:00:00Z")
}
content_copy
Use code with caution.
Json

2. Journal Entries Collection

Records every financial transaction with its corresponding debit and credit entries, ensuring double-entry bookkeeping.

{
    "_id": ObjectId("..."),
    "journalEntryNumber": "JE001",
    "transactionDate": ISODate("2024-08-11T12:00:00Z"),
    "description": "Sale of goods",
    "transactionReference": "TXN001", // Reference to the original transaction (Sales, Purchase, etc.)
    "entries": [
        {
            "accountId": ObjectId("..."), // Reference to Chart of Accounts
            "debit": 99.98,  // Value will be on either debit or credit
            "credit": 0 
        },
        {
            "accountId": ObjectId("..."), 
            "debit": 0,
            "credit": 99.98
        }
        // ... more entries to complete the double-entry
    ],
    "createdAt": ISODate("2024-08-11T12:00:00Z"),
    "updatedAt": ISODate("2024-08-11T12:00:00Z")
}
content_copy
Use code with caution.
Json

3. General Ledger Collection (Optional)

While derivable from journal entries, a separate collection can speed up reporting. It provides a chronological record of all transactions for each account.

{
    "_id": ObjectId("..."),
    "accountId": ObjectId("..."),
    "transactionDate": ISODate("2024-08-11T12:00:00Z"),
    "journalEntryNumber": "JE001", 
    "description": "Sale of goods",
    "debit": 99.98,
    "credit": 0,
    "balance": 1500.00  // Running balance for the account after this transaction
}
content_copy
Use code with caution.
Json

Additional Considerations:

Tax Management: While you have a tax field in the Sales collection, a more robust system might involve a separate collection to handle various tax rates, jurisdictions, and tax calculations for different products.

Reporting: Consider how you will generate common accounting reports like:

Income Statement (Profit & Loss): Summarizes revenues and expenses over a period.

Balance Sheet: Provides a snapshot of assets, liabilities, and equity at a specific point in time.

Cash Flow Statement: Tracks the movement of cash in and out of the business.

Data Archiving: Implement strategies to archive or summarize historical accounting data for performance and compliance.

By incorporating these additions, your NoSQL database will be well-equipped to handle the complexities of a full-fledged accounting system alongside your POS and inventory management functionalities.
