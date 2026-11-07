-- 0010_device_release.sql (specs/02 section 12 "Device release on logout"; 00 decision 117, issue #293): POST /auth/logout
-- releases this device's binding after revoking its refresh family, so the next POST /auth/guest from the device makes a
-- new guest. The row is kept (saves and refresh_tokens reference it); a released row is rebound to the new guest.
alter table devices add column released_at timestamptz;
