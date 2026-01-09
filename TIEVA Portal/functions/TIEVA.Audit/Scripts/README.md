# TIEVA Audit Scripts - Development Guide

## MANDATORY REQUIREMENTS FOR NEW MODULES

When creating a new audit module, the following requirements **MUST** be met:

### 1. Findings Sheet (REQUIRED)

Every audit script **MUST** include a 'Findings' worksheet with the following exact columns:

| Column | Type | Description |
|--------|------|-------------|
| SubscriptionName | string | Name of the Azure subscription |
| SubscriptionId | string | GUID of the subscription |
| Severity | string | Must be: 'High', 'Medium', or 'Low' |
| Category | string | Category of the finding (e.g., 'Security', 'Compliance') |
| ResourceType | string | Type of Azure resource |
| ResourceName | string | Name of the affected resource |
| ResourceId | string | Full Azure resource ID |
| Detail | string | Description of the issue found |
| Recommendation | string | Suggested remediation action |

**Example:**
```powershell
$findings = [System.Collections.ArrayList]::new()

$findings.Add([PSCustomObject]@{
  SubscriptionName = $sub.Name
  SubscriptionId   = $sub.Id
  Severity         = 'High'
  Category         = 'Security'
  ResourceType     = 'Virtual Machine'
  ResourceName     = $vm.Name
  ResourceId       = $vm.Id
  Detail           = "VM has critical security vulnerability"
  Recommendation   = 'Apply security patches immediately'
}) | Out-Null

# Export at the end
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'
```

### 2. Why This Is Required

The portal's auto-parse function reads the 'Findings' sheet to:
- Display finding counts in the UI (High/Medium/Low)
- Calculate assessment scores
- Show individual findings in the Findings tab
- Aggregate data for the dashboard

**Without the Findings sheet, the module will run but:**
- ❌ No findings will appear in the portal
- ❌ No severity breakdown will be shown
- ❌ No score will be calculated
- ❌ Dashboard won't include this module's data

### 3. Checklist for New Modules

- [ ] Script follows naming convention: `{Module}Audit.ps1`
- [ ] Output file follows convention: `{Module}_Audit.xlsx`
- [ ] `$findings` ArrayList initialized at start
- [ ] Findings added throughout the script as issues are detected
- [ ] 'Findings' sheet exported with exact column names
- [ ] Script added to `run.ps1` scriptMap and outputFileMap
- [ ] Module added to database (AssessmentModules table)
- [ ] Module icons added to portal index.html (moduleIcons object)
- [ ] Module filter tab added to portal UI
- [ ] TierModules mappings configured in database

### 4. Standard Export Helper Function

Use this helper function in all scripts:

```powershell
function Export-Sheet { 
  param($Data, $WorksheetName, $TableName)
  if (-not $Data -or $Data.Count -eq 0) { 
    Write-Host "  Skipping empty: $WorksheetName" -ForegroundColor Gray
    return 
  }
  $Data | Export-Excel -Path $outputFile -WorksheetName $WorksheetName `
    -TableName $TableName -TableStyle 'Medium9' -AutoSize -FreezeTopRow -BoldTopRow
  Write-Host "  + $WorksheetName ($($Data.Count) rows)" -ForegroundColor Green
}
```

### 5. Files to Update When Adding a New Module

1. **Database:**
   - `AssessmentModules` - Add module record
   - `TierModules` - Map to service tiers

2. **TIEVA.Audit Function App:**
   - `Scripts/{Module}Audit.ps1` - Create audit script
   - `StartAssessment/run.ps1` - Add to scriptMap and outputFileMap
   - `requirements.psd1` - Add any new Az modules needed

3. **Portal:**
   - `index.html` - Add module filter tab
   - `index.html` - Add to moduleIcons object (multiple locations)
   - `index.html` - Add to defaultModules array

---

## Existing Modules Reference

| Module | Script | Output File |
|--------|--------|-------------|
| NETWORK | NetworkAudit.ps1 | Network_Audit.xlsx |
| BACKUP | BackupAudit.ps1 | Backup_Audit.xlsx |
| COST | CostManagementAudit.ps1 | Cost_Management_Audit.xlsx |
| IDENTITY | IdentityAudit.ps1 | Identity_Audit.xlsx |
| POLICY | PolicyAudit.ps1 | Policy_Audit.xlsx |
| RESOURCE | ResourceAudit.ps1 | Resource_Audit.xlsx |
| RESERVATION | ReservationAudit.ps1 | Reservation_Audit.xlsx |
| SECURITY | SecurityAudit.ps1 | Security_Audit.xlsx |
| PATCH | PatchAudit.ps1 | Patch_Audit.xlsx |
| PERFORMANCE | PerformanceAudit.ps1 | Performance_Audit.xlsx |
| COMPLIANCE | ComplianceAudit.ps1 | Compliance_Audit.xlsx |
