# TIEVA Portal - Issues & Solutions Log

**Last Updated:** January 2025

This document tracks common issues encountered and their solutions.

---

## Deployment Issues

### Portal Changes Not Reflecting (Cache Issue)

**Symptom:** Git shows "nothing to commit" but changes aren't appearing in portal.

**Cause:** Azure Static Web Apps aggressively caches index.html.

**Solution:** Add timestamp comment to force cache invalidation:
```html
<!-- Deployed: 2025-01-05 15:30:00 - Description of changes -->
<!DOCTYPE html>
```

---

### .NET Function Deployment Fails

**Symptom:** `func azure functionapp publish` fails with authentication error.

**Solution:**
```powershell
# Re-authenticate
az login
az account set --subscription "your-subscription-id"

# Then deploy
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Functions"
.\deploy.ps1
```

---

### PowerShell Function Cold Start Timeout

**Symptom:** First assessment after idle period times out.

**Cause:** PowerShell function app cold start + module loading.

**Solution:** 
- Increase client timeout to 600 seconds
- Consider Premium plan for always-warm instances
- Add warming requests before important runs

---

## Database Issues

### SQL Connection Fails with Managed Identity

**Symptom:** "Login failed for user '<token-identified principal>'"

**Solution:**
1. Verify managed identity is enabled on Function App
2. Add identity as database user:
```sql
CREATE USER [func-tievaPortal-6612] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [func-tievaPortal-6612];
ALTER ROLE db_datawriter ADD MEMBER [func-tievaPortal-6612];
```

---

### Entity Framework Migration Issues

**Symptom:** Table/column doesn't exist errors after schema changes.

**Solution:** Run migrations or manual SQL:
```powershell
# In TIEVA.Functions directory
dotnet ef database update
```

Or apply manual SQL via Azure Portal Query Editor.

---

## Azure Connection Issues

### Get-AzRoleAssignment Returns Blank DisplayName

**Symptom:** Role assignments for groups show blank DisplayName.

**Cause:** Inherited assignments or cross-tenant groups.

**Solution:** Implement fallback lookup:
```powershell
$assignment = Get-AzRoleAssignment -ObjectId $id
if ([string]::IsNullOrEmpty($assignment.DisplayName)) {
    try {
        $group = Get-AzADGroup -ObjectId $assignment.ObjectId
        $displayName = $group.DisplayName
    } catch {
        $displayName = $assignment.ObjectId
    }
}
```

---

### Service Principal Secret Expiry

**Symptom:** Connection validation fails after working previously.

**Cause:** SP secret expired.

**Solution:**
1. Generate new secret in customer's App Registration
2. Update connection in portal (or delete and recreate)
3. Secret stored in Key Vault is automatically updated

---

## Portal UI Issues

### Findings Not Displaying After Assessment

**Symptom:** Assessment completes but findings list is empty.

**Possible Causes:**
1. **Parsing error** - Check module result for error message
2. **Sheet name mismatch** - PowerShell exports vs expected name
3. **Column name mismatch** - Check Findings worksheet columns

**Solution:** 
1. Download Excel and verify "Findings" sheet exists
2. Check column headers match expected: SubscriptionName, SubscriptionId, Severity, Category, ResourceType, ResourceName, Finding, Recommendation
3. Use re-parse button to retry

---

### Module Filter Not Working

**Symptom:** Clicking module tab doesn't filter findings.

**Cause:** ModuleCode field mismatch between findings and filter.

**Debug:**
```javascript
// In browser console
console.log('Filter:', currentModuleFilter);
console.log('Available:', [...new Set(currentCustomerFindings.map(f => f.moduleCode))]);
```

**Solution:** Ensure moduleCode is uppercase and consistent.

---

### Score Shows Wrong Value

**Symptom:** Different scores shown in different places.

**Cause (Fixed Dec 2024):** Was showing latest vs lowest vs average inconsistently.

**Current Behavior:** All locations now show average score across assessments.

---

## API Issues

### CORS Errors

**Symptom:** Browser console shows CORS errors.

**Solution:** Verify host.json in Function App:
```json
{
  "Host": {
    "CORS": {
      "AllowedOrigins": [
        "https://ambitious-wave-092ef1703.3.azurestaticapps.net",
        "http://localhost:3000"
      ]
    }
  }
}
```

---

### API Returns 500 Error

**Symptom:** Generic server error on API call.

**Debug:**
1. Check Function App logs in Azure Portal → Monitor → Logs
2. Check Application Insights for detailed exception
3. Test endpoint directly with curl/Postman

---

## Audit Script Issues

### Module Produces Empty Results

**Symptom:** Excel file created but no data rows.

**Possible Causes:**
1. No resources of that type exist
2. Permissions insufficient
3. Script error silently caught

**Solution:**
1. Run script manually with verbose output
2. Check subscription has expected resources
3. Verify SP has Reader role on all subscriptions

---

### ImportExcel Module Not Found

**Symptom:** "The term 'Export-Excel' is not recognized"

**Solution:** Verify requirements.psd1 includes:
```powershell
@{
    'Az.Accounts' = '2.*'
    'Az.Resources' = '6.*'
    'ImportExcel' = '7.*'
}
```

Then redeploy function app.

---

## Character Encoding Issues

### Em-dashes Display as Corrupted Characters

**Symptom:** "â€"" appears instead of "—"

**Cause:** UTF-8 encoding not properly set.

**Solution:** 
1. Ensure HTML has `<meta charset="utf-8">`
2. Save files with UTF-8 encoding
3. Replace em-dashes with regular dashes if needed

---

## Performance Issues

### Dashboard Load Slow (> 3 seconds)

**Causes:**
- Too many assessments loading
- CustomerFindings table large
- No pagination

**Solutions (implemented):**
- Parallel API calls with Promise.all()
- O(1) lookup Maps for customers/connections
- Debounced filter functions
- DOM reference caching

**Future Solutions (if needed):**
- Server-side pagination
- Virtual scrolling for large lists
- Redis caching layer

---

## Cascading Delete Issues

### Orphaned Records After Delete

**Symptom:** Related records remain after parent deleted.

**Solution:** Ensure proper cascade order:
1. Delete deepest children first (Findings)
2. Work up the hierarchy
3. Delete parent last

**Current Implementation:**
- Customer delete: CustomerFindings → Findings → ModuleResults → Assessments → Subscriptions → Connections → Customer
- Connection delete: Findings → ModuleResults → Assessments → Subscriptions → Connection
- Assessment delete: Findings → ModuleResults → Assessment

---

## Frontend Timeout Issues

### Assessment Takes Too Long (>4 minutes)

**Symptom:** Frontend shows error but assessment actually completes in the backend.

**Cause:** Azure Static Web App has ~4 minute timeout limit for proxied requests.

**Solution (Implemented Jan 2025):**
- Use async queue-based processing
- StartAssessment queues job and returns immediately
- ProcessAssessment (queue trigger) runs audits
- Frontend polls for completion status

---

### Reservation Refresh Timeout

**Symptom:** Clicking "Refresh Data from Azure" shows error but data eventually appears.

**Cause:** Azure Consumption/Reservation APIs can be slow (30+ seconds).

**Solution (Implemented Jan 2025):**
- CustomerReservationCache table stores async results
- Refresh triggers async API fetch
- Frontend polls status until complete
- Cache refreshed in background

---

## SWA Linking Issues

### External API Calls Blocked

**Symptom:** func-tieva-audit cannot call func-tievaportal-6612 API.

**Cause:** SWA linking blocks external (non-browser) API calls.

**Solution:** 
- Keep SWA-linked endpoints for browser requests (security benefit)
- Audit functions write directly to database instead of API callbacks
- Use direct database operations for backend-to-backend communication

---

## FinOps Issues

### SAS Token Validation Fails

**Symptom:** "Invalid SAS token" error for Azure-generated tokens.

**Cause:** Portal validation expected `sv=` prefix but Azure tokens may start with `sp=`.

**Solution:** Update validation to accept tokens starting with any valid parameter.

---

### Cost Data Shows Wrong Values

**Symptom:** 30-day cost is much higher than expected.

**Cause:** Multiple parquet files being read, causing duplication.

**Solution (Fixed Jan 2025):**
- Read all files in date range
- Deduplicate records at row level
- Use composite key for deduplication

---

### Export Discovery Returns Empty

**Symptom:** "Run Export Now" shows 0 exports found.

**Cause:** Cost Management exports may be at subscription or billing scope level.

**Debug:**
```powershell
# Check both subscription and billing scope
GET https://management.azure.com/subscriptions/{id}/providers/Microsoft.CostManagement/exports
GET https://management.azure.com/providers/Microsoft.Billing/billingAccounts/{id}/providers/Microsoft.CostManagement/exports
```

---

## Column Mapping Issues

### Finding Column Name Mismatch

**Symptom:** Findings not appearing in portal after parsing.

**Cause:** Database column is `Finding` but EF property is `FindingText`.

**Solution:**
- PowerShell scripts must insert into `Finding` column
- EF mapping in TievaDbContext.cs: `e.Property(x => x.FindingText).HasColumnName("Finding")`
- Don't change database schema; align code to existing mapping

---

## Known Limitations

1. **Single-threaded Audit:** Modules run sequentially, not in parallel
2. **Async Polling Required:** Portal must poll for assessment/reservation status
3. **Basic SQL Tier:** May hit DTU limits with heavy concurrent use
4. **No Retry Logic:** Failed modules don't automatically retry
5. **File Size Limit:** Very large Excel files may timeout during parse
6. **SWA Proxy Timeout:** ~4 minute limit for synchronous requests
7. **Reservation API Rate Limits:** Azure Consumption APIs may be slow
