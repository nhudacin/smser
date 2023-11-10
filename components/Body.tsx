'use client';

import * as z from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { useForm } from 'react-hook-form';
import {
    Form,
    FormControl,
    FormDescription,
    FormField,
    FormItem,
    FormLabel,
    FormMessage,
} from '@/components/ui/form';
import { Textarea } from '@/components/ui/textarea';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { useCallback, useEffect, useState } from 'react';
// import { QrGenerateRequest, QrGenerateResponse } from '@/utils/service';
import { QrCard } from '@/components/QrCard';
import { AlertCircle } from 'lucide-react';
import { Alert, AlertTitle, AlertDescription } from '@/components/ui/alert';
import LoadingDots from '@/components/ui/loadingdots';
// import downloadQrCode from '@/utils/downloadQrCode';
import va from '@vercel/analytics';
// import { garbledTextSuggestion } from '@/components/garbledTextSuggestion';
import { useRouter } from 'next/navigation';
import { toast, Toaster } from 'react-hot-toast';
import qrcode from 'qrcode-generator';

const generateFormSchema = z.object({
    groupName: z.string().min(1),
    garbledText: z.string().min(3).max(160),
    formattedNumbers: z.string().min(9)
});

type GenerateFormValues = z.infer<typeof generateFormSchema>;

const Body = ({
    groupName,
    garbledText,
    formattedNumbers,
    id,
}: {
    groupName?: string;
    garbledText?: string;
    formattedNumbers?: string;
    id?: string;
}) => {
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<Error | null>(null);
    const [response, setResponse] = useState<string | null>(null);
    const [submittedURL, setSubmittedURL] = useState<string | null>(null);

    const router = useRouter();

    const form = useForm<GenerateFormValues>({
        resolver: zodResolver(generateFormSchema),
        mode: 'onChange',

        // Set default values so that the form inputs are controlled components.
        defaultValues: {
            groupName: '',
            garbledText: '',
            formattedNumbers: ''
        },
    });

    useEffect(() => {
        if (groupName && garbledText && formattedNumbers) {
            // setResponse({
            //     groupName: groupName,
            //     formattedNumbers: formattedNumbers,
            //     garbledText: garbledText,
            //     id: id,
            // });

            //form.setValue('garbledText', garbledText);
            //form.setValue('formattedNumbers', formattedNumbers);

            console.log("end of useEffect")
        }
    }, [groupName, garbledText, formattedNumbers, id, form]);

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

    function getQRCode(smsLink: string) {
        var qr = qrcode(0, "L");
        qr.addData("'" + smsLink + "'"); // surrounded by single quotes - DO NOT DELETE
        qr.make();

        return qr
    }

    const handleSubmit = useCallback(
        async (values: GenerateFormValues) => {
            console.log("hello")
            const smsUrl = getSMSUrl(values.formattedNumbers)
            const qrCode = getQRCode(smsUrl);

            setResponse(qrCode.createDataURL());

            console.log(smsUrl);
            setIsLoading(true);

            setSubmittedURL(smsUrl);

            try {
                // const request: QrGenerateRequest = {
                //     url: values.url,
                //     garbledText: values.garbledText,
                // };
                // const response = await fetch('/api/generate', {
                //     method: 'POST',
                //     body: JSON.stringify(request),
                // });

                // // Handle API errors.
                // if (!response.ok || response.status !== 200) {
                //     const text = await response.text();
                //     throw new Error(
                //         `Failed to generate QR code: ${response.status}, ${text}`,
                //     );
                // }

                // const data = await response.json();

                // va.track('Generated QR Code', {
                //     garbledText: values.garbledText,
                // });

                // router.push(`/start/${data.id}`);
            } catch (error) {
                va.track('Failed to generate', {
                    garbledText: values.garbledText,
                });
                if (error instanceof Error) {
                    setError(error);
                }
            } finally {
                setIsLoading(false);
            }
        },
        [router],
    );

    const handleImport = useCallback(
        () => {
            try {
                var garbledTextRaw = form.getValues('garbledText')
                var garbledTextSplit = garbledTextRaw.split(/\r?\n/);
                var numbersFormatted = [];

                for (var i = 0; i < garbledTextSplit.length; i++) {
                    var thenum = garbledTextSplit[i].replace(/\D+/g, ''); // Replace all leading non-digits with nothing

                    if (thenum.length >= 10) {
                        // there is a number!
                        if (thenum.length == 10) {
                            thenum = "1" + thenum;
                        }
                        else if (thenum.length >= 11) {
                            thenum = thenum;
                        }

                        if (numbersFormatted.indexOf(thenum) === -1) {
                            numbersFormatted.push(thenum);
                        }
                    }
                }
                var returnObject = numbersFormatted.join("\r\n")
                form.setValue('formattedNumbers', returnObject);

            } catch (error) {
                va.track('Failed to generate', {
                    garbledText: form.getValues('garbledText'),
                });
                if (error instanceof Error) {
                    setError(error);
                }
            } finally {
                setIsLoading(false);
            }
        },
        [form],
    );

    return (
        <div className="flex justify-center items-center flex-col w-full lg:p-0 p-4 sm:mb-28 mb-0">
            <div className="max-w-6xl w-full grid grid-cols-1 md:grid-cols-2 gap-6 md:gap-12 mt-20">
                <div className="col-span-1">
                    <h1 className="text-3xl font-bold mb-10">New SMS Group</h1>
                    <Form {...form}>
                        <form onSubmit={form.handleSubmit(handleSubmit)}>
                            <div className="flex flex-col gap-4">
                                <FormField
                                    control={form.control}
                                    name="groupName"
                                    render={({ field }) => (
                                        <FormItem>
                                            <FormLabel>SMS Group Name</FormLabel>
                                            <FormControl>
                                                <Input placeholder="soccer team 2023" {...field} />
                                            </FormControl>
                                            <FormDescription>
                                                This helps organize multiple teams/rosters
                                            </FormDescription>
                                            <FormMessage />
                                        </FormItem>
                                    )}
                                />
                                <FormField
                                    control={form.control}
                                    name="garbledText"
                                    render={({ field }) => (
                                        <FormItem>
                                            <FormLabel>Roster Import</FormLabel>
                                            <FormControl>
                                                <Textarea
                                                    placeholder="Single press this box and click the icon next to 'Select All' to have your camera scan the numbers"
                                                    className="resize-none"
                                                    {...field}
                                                />
                                            </FormControl>
                                            <FormDescription className="">
                                                You can paste everything your phone will "copy as text" into the above box. It will automatically
                                                pull the cell phone numbers from the garbled up text.
                                            </FormDescription>

                                            <FormMessage />
                                        </FormItem>
                                    )}
                                />

                                <div className="my-2">
                                    <Button
                                        type="button"
                                        onClick={() => handleImport()}
                                        className="inline-flex justify-center
                 max-w-[200px] mx-auto w-full"
                                    >
                                        Import
                                    </Button>
                                </div>

                                <FormField
                                    control={form.control}
                                    name="formattedNumbers"
                                    render={({ field }) => (
                                        <FormItem>
                                            <FormLabel>Phone Numbers</FormLabel>
                                            <FormControl>
                                                <Textarea
                                                    placeholder="Single press this box and click the icon next to 'Select All' to have your camera scan the numbers"
                                                    className="resize-none"
                                                    rows={10}
                                                    // need to make this box larger
                                                    {...field}
                                                />
                                            </FormControl>
                                            <FormDescription className="">
                                                Phone Numbers, 1 per line
                                                <br />
                                                <b>IMPORTANT:</b> Remove YOUR phone number from the above list. If it exists.
                                            </FormDescription>

                                            <FormMessage />
                                        </FormItem>
                                    )}
                                />

                                <div className="my-2">

                                </div>
                                <Button
                                    type="submit"
                                    disabled={isLoading}
                                    className="inline-flex justify-center
                 max-w-[200px] mx-auto w-full"
                                >
                                    {isLoading ? (
                                        <LoadingDots color="white" />
                                    ) : response ? (
                                        '✨ Regenerate'
                                    ) : (
                                        'Generate'
                                    )}
                                </Button>

                                {error && (
                                    <Alert variant="destructive">
                                        <AlertCircle className="h-4 w-4" />
                                        <AlertTitle>Error</AlertTitle>
                                        <AlertDescription>{error.message}</AlertDescription>
                                    </Alert>
                                )}
                            </div>
                        </form>
                    </Form>
                </div>
                <div className="col-span-1">
                    {submittedURL && (
                        <>
                            <h1 className="text-3xl font-bold sm:mb-5 mb-5 mt-5 sm:mt-0 sm:text-center text-left">
                                Your QR Code
                            </h1>
                            <div>
                                <div className="flex flex-col justify-center relative h-auto items-center">
                                    {response ? (
                                        <QrCard
                                            imageURL={response}
                                            time="testing"
                                        />
                                    ) : (
                                        <div className="relative flex flex-col justify-center items-center gap-y-2 w-[510px] border border-gray-300 rounded shadow group p-2 mx-auto animate-pulse bg-gray-400 aspect-square max-w-full" />
                                    )}
                                </div>
                                {response && (
                                    <div className="flex justify-center gap-5 mt-4">
                                        <a href={submittedURL}>
                                            <button
                                                type="button"
                                                className="rounded-md bg-indigo-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                                            >
                                                Mobile Link
                                            </button>
                                        </a>
                                        <Button
                                            variant="outline"
                                            onClick={() => {
                                                navigator.clipboard.writeText(
                                                    `https://smser.io/start/${id || ''}`,
                                                );
                                                toast.success('Link copied to clipboard');
                                            }}
                                        >
                                            ✂️ Share
                                        </Button>
                                    </div>
                                )}
                            </div>
                        </>
                    )}
                </div>
            </div>
            <Toaster />
        </div >
    );
};

export default Body;
