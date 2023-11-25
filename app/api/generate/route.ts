import { SMSUrlRequest, SMSUrlResponse } from '@/utils/interfaces';
import { NextRequest } from 'next/server';
import { kv } from '@vercel/kv';
import { nanoid } from '@/utils/utils';
import qrcode from 'qrcode-generator';

/**
 * Validates a request object.
 *
 * @param {SMSUrlRequest} request - The request object to be validated.
 * @throws {Error} Error message if URL or prompt is missing.
 */

const validateRequest = (request: SMSUrlRequest) => {
    if (!request.formattedNumbers) {
        throw new Error('formattedNumbers is required');
    }
    if (!request.groupName) {
        throw new Error('groupName is required');
    }
};

function getQRCode(smsLink: string) {
    var qr = qrcode(0, "L");
    qr.addData("'" + smsLink + "'"); // surrounded by single quotes - DO NOT DELETE
    qr.make();

    return qr
}

function getSMSUrl(numbers: string) {
    var numbersSplit = numbers.split(/\r?\n/);

    // build the link
    var smsLink = "sms://open?addresses=";
    for (var i = 0; i < numbersSplit.length; i++) {
        var numberToAdd = numbersSplit[i];

        // add a 1 if the country code is missing
        if (numberToAdd.length == 10) {
            numberToAdd = "1" + numberToAdd;
        }

        // if this is the first one, do NOT add the comma
        if (i != 0) {
            numberToAdd = "," + numberToAdd;
        }

        smsLink += numberToAdd;
    }

    // add a final single quote to the link
    smsLink += "";
    return smsLink;
}

export async function POST(request: NextRequest) {
    const reqBody = (await request.json()) as SMSUrlRequest;

    try {
        validateRequest(reqBody);
    } catch (e) {
        if (e instanceof Error) {
            return new Response(e.message, { status: 400 });
        }
    }

    const id = reqBody.id ? reqBody.id : nanoid();
    const startTime = performance.now();
    const smsUrl = getSMSUrl(reqBody.formattedNumbers);
    const qrCode = getQRCode(smsUrl);

    let imageUrl = qrCode.createDataURL();

    const endTime = performance.now();
    const durationMS = endTime - startTime;

    await kv.hset(id, {
        groupName: reqBody.groupName,
        image: imageUrl,
        formattedNumbers: reqBody.formattedNumbers,
        garbledText: reqBody.garbledText,
        model_latency: Math.round(durationMS),
    });

    const response: SMSUrlResponse = {
        image_url: imageUrl,
        model_latency_ms: Math.round(durationMS),
        id: id,
    };

    return new Response(JSON.stringify(response), {
        status: 200,
    });
}
