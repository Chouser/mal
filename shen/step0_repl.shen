(defun READ (x) x)
(defun EVAL (x) x)
(defun PRINT (x) x)
(defun rep (x)
  (PRINT (EVAL (READ x))))

(defun readln (s)
  (let x (read-byte)
    (if (= x 10)
        s
      (if (= x -1)
          nil
          (readln (cn s (n->string x)))))))

(defun writestr (s)
  (if (= "" s)
      nil
    (do
        (write-byte (string->n (pos s 0)) (stoutput))
        (writestr (tlstr s)))))

(defun writeln (s)
  (do
      (writestr s)
      (write-byte 10 (stoutput))))

(defun main ()
  (do
      (writestr "user> ")
      (let line (readln "")
           (if (= nil line)
               (writeln "")
               (do
                   (writeln (rep line))
                   (main))))))

(main)
