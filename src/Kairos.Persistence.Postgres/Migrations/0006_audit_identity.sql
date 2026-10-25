-- Who really did it. The audit log used to record the name the caller supplied. These columns are filled by the database itself in the INSERT
-- (session_user, inet_client_addr()), so a caller cannot choose them; claimed_actor keeps the supplied name apart, clearly as a claim, and
-- verified_principal holds the authenticated principal when the caller was given one.
ALTER TABLE kairos.audit_log
    ADD COLUMN session_user_name  text,
    ADD COLUMN client_addr        text,
    ADD COLUMN claimed_actor      text,
    ADD COLUMN verified_principal text;
