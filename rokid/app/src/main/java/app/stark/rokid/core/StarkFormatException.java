package app.stark.rokid.core;

/** Пакет не прошёл проверку формата: сообщение показывается сборщику как есть. */
public class StarkFormatException extends Exception {
    public StarkFormatException(String message) {
        super(message);
    }

    public StarkFormatException(String message, Throwable cause) {
        super(message, cause);
    }
}
