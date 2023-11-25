export interface SMSUrlRequest {
    /**
     * The formatted numbers of any request
     */
    formattedNumbers: string;

    /**
     * The Group Name
     */
    groupName: string;

    /**
     * Text used for import
     */
    garbledText?: string;

    /**
     * If ID is modified, this will come through in the request
     */
    id?: string;
}

export interface SMSUrlResponse {
    /**
     * URL of the QR code images that was generated by the model.
     */
    image_url: string;

    /**
     * Response latency in milliseconds.
     */
    model_latency_ms: number;

    /**
     * Unique ID of the QR code.
     * This ID can be used to retrieve the QR code image from the API.
     */
    id: string;
}
