# TIEVA Portal - Scalability Notes

*Last Updated: December 2024*

## Current Architecture

The portal uses a client-side rendering approach where all data is loaded into the browser on page load. This is simple and fast for small datasets but has known scaling limits.

### Tech Stack
- **Frontend:** Single-page HTML/JS app (no framework)
- **Backend:** Azure Functions (Consumption Plan)
- **Database:** Azure SQL
- **Storage:** Azure Blob Storage (audit results)

---

## Performance Thresholds

### Frontend (Browser) Limits

| Metric | Comfortable | Starts Lagging | Unusable |
|--------|-------------|----------------|----------|
| Customers | 50 | 100-200 | 500+ |
| Assessments (total) | 200 | 500-1000 | 2000+ |
| Findings per customer | 500 | 2000-5000 | 10,000+ |
| Findings in single view | 200 | 500 | 1000+ |

### Concurrent Users

| Users | Experience |
|-------|------------|
| 1-5 | Excellent |
| 5-15 | Good |
| 15-30 | Some cold-start delays |
| 30+ | Database connection limits, slower responses |

---

## Warning Signs

Watch for these indicators that scaling work is needed:

1. **Dashboard takes 3-5+ seconds to load**
2. **Customer detail page hangs when loading findings**
3. **Filter changes feel sluggish (>500ms delay)**
4. **Browser memory warnings or tab crashes**
5. **API timeouts on large data fetches**

---

## When to Implement Scaling Solutions

### Trigger Points

- [ ] 50+ customers
- [ ] Any single customer has 2000+ findings
- [ ] Dashboard load time exceeds 3 seconds
- [ ] 10+ concurrent users regularly
- [ ] Total assessments exceed 500

### Recommended Solutions (by priority)

| Problem | Solution | Effort | Impact |
|---------|----------|--------|--------|
| All data loads at once | Lazy load details on demand | Low | High |
| Too many findings rendering | Add pagination to findings lists | Medium | High |
| Slow initial load | Server-side pagination for assessments | Medium | High |
| Cold starts affecting users | Move to App Service Basic plan | Low (cost) | Medium |
| Database bottleneck | Add Redis caching layer | Medium | Medium |
| Very large finding lists | Virtual scrolling (render visible only) | Medium | High |

---

## Optimizations Already Implemented

### December 2024
- [x] Parallel API calls with `Promise.all()` in subscription saves
- [x] O(1) lookup Maps for customers/connections (replaces `.find()` loops)
- [x] Debounced filter functions (prevents rapid re-renders)
- [x] Initial data load uses `Promise.all()` for parallel fetching

### Still Using Simple Approach (OK for now)
- Full data load on page init (no pagination)
- Client-side filtering (no server-side)
- Full DOM re-renders on filter change
- No request caching

---

## Estimated Timeline

Based on typical usage patterns:

| Timeframe | Expected State | Action Needed |
|-----------|----------------|---------------|
| Now - 6 months | 5-20 customers, <500 assessments | None |
| 6-12 months | 20-50 customers, 500-1000 assessments | Monitor performance |
| 12-18 months | 50+ customers, 1000+ assessments | Implement pagination |
| 18+ months | Scale limits | Consider architecture review |

---

## Quick Reference: Future Scaling Work

When ready to implement pagination, start here:

1. **API Changes:**
   - Add `?page=1&pageSize=50` to `/assessments` endpoint
   - Add `?page=1&pageSize=100` to `/customers/{id}/findings` endpoint
   - Return `{ data: [], totalCount: N, page: N, pageSize: N }`

2. **Frontend Changes:**
   - Add pagination controls to tables
   - Implement "Load More" or page numbers
   - Update filter functions to call API instead of client-side filter

3. **Quick Wins First:**
   - Lazy load customer findings (only when tab clicked)
   - Lazy load assessment details (only when clicked)
   - Add loading spinners for async operations

---

## Contact

Architecture questions: Review with team before major changes.
