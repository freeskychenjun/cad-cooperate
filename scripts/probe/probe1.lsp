;; probe1.lsp - ASCII only. Loaded into CAD via SendCommand (load "...")
;; Global results are read back by the agent through Document.GetLispVariable

(defun prb:ms ( / d)
  (setq d (vl-catch-all-apply 'getvar (list "MILLISECS")))
  (if (vl-catch-all-error-p d)
    (* 86400000.0 (getvar "DATE"))
    (float d)
  )
)

;; @@PRB_DIR@@ is substituted by the probe script at runtime (env vars do NOT
;; propagate: CAD is launched by the COM SCM, not by our shell)
(defun prb:p (n) (strcat "@@PRB_DIR@@" "/" n))

(defun c:PRBHELLO () (setq PRB_HELLO "hello-from-lisp") (princ))

;; build a long string of entity handles: handle,handle,...
(defun c:PRB-HSTR ( / e s)
  (setq s "" e (entnext))
  (while e
    (setq s (strcat s (cdr (assoc 5 (entget e))) ","))
    (setq e (entnext e))
  )
  (setq PRB_HSTR s)
  (princ)
)

;; dump full entity table as one CSV string: handle|type|layer|x1|y1|x2|y2;
(defun c:PRB-DUMP ( / e s d h ty la p1 p2)
  (setq s "" e (entnext))
  (while e
    (setq d (entget e) h (cdr (assoc 5 d)) ty (cdr (assoc 0 d)) la (cdr (assoc 8 d)))
    (setq p1 (cdr (assoc 10 d)) p2 (cdr (assoc 11 d)))
    (setq s (strcat s h "|" ty "|" la "|"
                    (if p1 (strcat (rtos (car p1) 2 3) "," (rtos (cadr p1) 2 3)) "")
                    "|" (if p2 (strcat (rtos (car p2) 2 3) "," (rtos (cadr p2) 2 3)) "") ";"))
    (setq e (entnext e))
  )
  (setq PRB_DUMP s)
  (princ)
)

;; write the same CSV to a file (fallback channel)
(defun c:PRB-DUMPF ( / f)
  (c:PRB-DUMP)
  (setq f (open (prb:p "dump.csv") "w"))
  (write-line PRB_DUMP f)
  (close f)
  (setq PRB_DUMPF "ok")
  (princ)
)

;; create N lines by entmake in a loop; N comes from PRB_N
(defun c:PRB-MKN ( / i t0 t1)
  (setq t0 (prb:ms) i 0)
  (while (< i PRB_N)
    (entmake (list (cons 0 "LINE")
                   (cons 10 (list (float i) 0.0 0.0))
                   (cons 11 (list (float i) 10.0 0.0))
                   (cons 8 "PRB")))
    (setq i (1+ i))
  )
  (setq t1 (prb:ms))
  (setq PRB_MKN_MS (- t1 t0))
  (setq PRB_MKN_N i)
  (princ)
)

;; create N circles by the LINE/CIRCLE command path (measures command overhead)
(defun c:PRB-MKC ( / i t0 t1)
  (setq t0 (prb:ms) i 0)
  (while (< i PRB_N)
    (command "_.CIRCLE" (list (float i) 5.0 0.0) 0.5)
    (setq i (1+ i))
  )
  (setq t1 (prb:ms))
  (setq PRB_MKC_MS (- t1 t0))
  (princ)
)

;; count entities via LISP
(defun c:PRB-CNT ( / e n)
  (setq n 0 e (entnext))
  (while e (setq n (1+ n)) (setq e (entnext e)))
  (setq PRB_CNT n)
  (princ)
)

;; simple echo with timing probe
(defun c:PRB-TICK () (setq PRB_TICK (prb:ms)) (princ))
