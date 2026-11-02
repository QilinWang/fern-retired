INSERT INTO runs (id, dataset, model_type, seed, pred_len, seq_len, noise_sigma, status, source, artifacts, pinned, run_hash, success_hash, mse, artifact_dir, finished_at, machine_alias, target_hash)
VALUES
 (1,'lorenz','dlinear',1,64,336,0.0,'done','recipe','full',0,'run-done-a','run-done-a',0.5,'results/x/run-done-a','2026-08-01T10:00:00Z','local-4070','t1'),
 (2,'lorenz','dlinear',2,64,336,0.0,'done','recipe','slim',0,'run-done-b','run-done-b',0.75,'results/x/run-done-b','2026-08-01T11:00:00Z','local-4070','t2'),
 (3,'lorenz','dlinear',3,64,336,0.0,'done','user_defined','full',1,'run-drift','run-drift-OLD',1.25,'results/x/run-drift','2026-08-01T12:00:00Z','pod-5090','t3'),
 (4,'lorenz','dlinear',4,64,336,0.0,'failed','recipe','none',0,'run-failed',NULL,NULL,NULL,'2026-08-01T13:00:00Z','local-4070','t4'),
 (5,'lorenz','dlinear',5,64,336,0.0,'stale','recipe','none',0,'run-stale',NULL,NULL,NULL,'2026-08-01T14:00:00Z',NULL,'t5'),
 (6,'lorenz','dlinear',6,64,336,0.0,'pending','recipe','none',0,'run-pending',NULL,NULL,NULL,NULL,NULL,'t6'),
 (7,'lorenz','dlinear',7,64,336,0.0,'running','recipe','none',0,'run-running',NULL,NULL,NULL,NULL,'pod-5090','t7'),
 (8,'lorenz','dlinear',8,64,336,0.0,'pending','user_defined','none',0,'run-pending-kept','run-pending-kept',NULL,NULL,NULL,NULL,'t8'),
 (9,'rossler','fern',9,64,336,0.25,'done','user_defined','slim',0,'run-done-c','run-done-c',2.0,'results/x/run-done-c','2026-08-02T10:00:00Z','local-4070',NULL);
